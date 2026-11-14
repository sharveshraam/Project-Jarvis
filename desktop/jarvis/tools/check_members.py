"""Best-effort cross-file member check for the C# sources (no .NET SDK needed).

A deliberately small "does this member exist / does this call have a plausible argument count"
checker built on tree-sitter. It resolves the static type of an expression only when that is cheap
(fields, parameters, typed locals, `var x = new T()`, awaited project methods, simple LINQ) and stays
silent when it cannot. It exists because the sandbox this code was written in has no compiler; it is
NOT a substitute for the real build, which runs in CI on windows-latest.

Usage: python tools/check_members.py [-v]
"""
import pathlib
import re
import sys
from collections import defaultdict

import tree_sitter_c_sharp
from tree_sitter import Language, Parser

ROOT = pathlib.Path(__file__).resolve().parent.parent
PARSER = Parser(Language(tree_sitter_c_sharp.language()))
VERBOSE = "-v" in sys.argv

TYPE_DECLS = {"class_declaration", "interface_declaration", "record_declaration", "struct_declaration", "enum_declaration"}
UNIVERSAL = {"ToString", "Equals", "GetHashCode", "GetType", "CompareTo", "HasFlag", "Deconstruct"}
# External base types whose members we know well enough to still treat the derived type as fully known.
KNOWN_EXTERNAL = {
    "IDisposable": {"Dispose"},
    "IAsyncDisposable": {"DisposeAsync"},
    "INotifyPropertyChanged": {"PropertyChanged"},
    "ICommand": {"Execute", "CanExecute", "CanExecuteChanged"},
    "IValueConverter": {"Convert", "ConvertBack"},
    "ILoggerProvider": {"CreateLogger", "Dispose"},
    "ILogger": {"Log", "IsEnabled", "BeginScope"},
}
SEQ = {"IReadOnlyList", "List", "IEnumerable", "IList", "ICollection", "IReadOnlyCollection", "ObservableCollection",
       "HashSet", "Queue", "Stack", "IOrderedEnumerable", "ConcurrentBag", "ISet", "SortedSet", "LinkedList", "ImmutableArray"}
SAME_SEQ = {"Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Take", "Skip", "Distinct", "Reverse",
            "Concat", "ToList", "ToArray", "AsEnumerable", "TakeWhile", "SkipWhile", "Append", "Prepend", "Except", "Union",
            "Intersect", "FindAll", "DistinctBy", "TakeLast", "SkipLast", "GetRange", "AsReadOnly", "Order", "ToHashSet"}
ELEMENT = {"First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault", "ElementAt", "ElementAtOrDefault",
           "Find", "MaxBy", "MinBy", "Peek", "Dequeue", "Pop"}
LAMBDA_ELEMENT = SAME_SEQ | ELEMENT | {"Any", "All", "Count", "Select", "SelectMany", "Sum", "Max", "Min", "Average", "RemoveAll",
                                       "Exists", "FindIndex", "ToDictionary", "ToLookup", "GroupBy", "ForEach", "LongCount",
                                       "FindLast", "FindLastIndex", "TrueForAll"}


def text(node):
    return node.text.decode("utf-8", "replace") if node is not None else ""


def field(node, name):
    return node.child_by_field_name(name)


class Member:
    def __init__(self, kind, name, type_=None, params=None, variadic=False):
        self.kind, self.name, self.type, self.params, self.variadic = kind, name, type_, params or [], variadic

    def accepts(self, count, names):
        declared = [p[0] for p in self.params]
        if any(n not in declared for n in names):
            return False
        required = sum(1 for p in self.params if not p[2])
        if self.variadic:
            return count >= required - 1
        return required <= count <= len(self.params)


class TypeInfo:
    def __init__(self, name, kind, path):
        self.name, self.kind, self.path = name, kind, path
        self.members = defaultdict(list)
        self.ctors = []
        self.bases = []
        self.type_params = set()
        self.nested = set()


TYPES = {}
AMBIGUOUS = set()
REPORTS = []
STATS = defaultdict(int)


def clean_type(t):
    if t is None:
        return None
    t = t.strip()
    while t.endswith("?"):
        t = t[:-1].strip()
    t = re.sub(r"\s+", "", t)
    return t or None


def split_generic(t):
    """'Dictionary<string,List<int>>' -> ('Dictionary', ['string', 'List<int>'])"""
    if t is None or "<" not in t or not t.endswith(">"):
        return t, []
    head, rest = t.split("<", 1)
    rest = rest[:-1]
    args, depth, current = [], 0, ""
    for ch in rest:
        if ch in "<(":
            depth += 1
        elif ch in ">)":
            depth -= 1
        if ch == "," and depth == 0:
            args.append(current)
            current = ""
        else:
            current += ch
    args.append(current)
    return head.split(".")[-1], [clean_type(a) for a in args]


def element_of(t):
    t = clean_type(t)
    if t is None:
        return None
    if t.endswith("[]"):
        return clean_type(t[:-2])
    head, args = split_generic(t)
    if head in SEQ and len(args) == 1:
        return args[0]
    return None


def project_type(t):
    t = clean_type(t)
    if t is None:
        return None
    head, _ = split_generic(t)
    head = head.split(".")[-1] if head else head
    if head in TYPES and head not in AMBIGUOUS:
        return TYPES[head]
    return None


def read_params(plist):
    params, variadic = [], False
    if plist is None:
        return params, variadic
    for child in plist.children:
        if child.type == "parameter":
            name = text(field(child, "name"))
            type_ = clean_type(text(field(child, "type")))
            # A default value shows up as an extra named child after the name.
            named = [c for c in child.named_children if c.type not in ("modifier", "attribute_list")]
            optional = len(named) > 2
            if any(text(c) == "params" for c in child.children):
                variadic = True
            params.append((name, type_, optional))
    # tree-sitter emits `params T[] x` as loose type/name children of the list.
    if any(text(c) == "params" for c in plist.children):
        loose = [c for c in plist.named_children if c.type != "parameter"]
        if len(loose) >= 2:
            params.append((text(loose[-1]), clean_type(text(loose[-2])), True))
        variadic = True
    return params, variadic


def collect_type(node, path, outer=None):
    name = text(field(node, "name"))
    info = TypeInfo(name, node.type.replace("_declaration", ""), path)
    if name in TYPES:
        AMBIGUOUS.add(name)
    TYPES[name] = info
    if outer is not None:
        outer.nested.add(name)
    for child in node.children:
        if child.type == "type_parameter_list":
            info.type_params = {text(c).split()[-1] for c in child.named_children}
        elif child.type == "base_list":
            info.bases = [split_generic(clean_type(text(c)))[0] for c in child.named_children if c.type != "argument_list"]
        elif child.type == "parameter_list":  # record primary constructor
            params, variadic = read_params(child)
            info.ctors.append(Member("ctor", name, name, params, variadic))
            for pname, ptype, _ in params:
                info.members[pname].append(Member("prop", pname, ptype))
    body = field(node, "body")
    if body is None:
        return
    for m in body.named_children:
        if m.type in TYPE_DECLS:
            collect_type(m, path, info)
        elif m.type == "enum_member_declaration":
            mname = text(field(m, "name"))
            info.members[mname].append(Member("enum", mname, name))
        elif m.type == "method_declaration":
            mname = text(field(m, "name"))
            params, variadic = read_params(field(m, "parameters"))
            rtype = clean_type(text(field(m, "returns") or field(m, "type")))
            generic = any(c.type == "type_parameter_list" for c in m.children)
            info.members[mname].append(Member("method", mname, None if generic else rtype, params, variadic))
        elif m.type == "constructor_declaration":
            params, variadic = read_params(field(m, "parameters"))
            if not any(text(c) == "static" for c in m.children if c.type == "modifier"):
                info.ctors.append(Member("ctor", name, name, params, variadic))
        elif m.type in ("property_declaration", "indexer_declaration"):
            mname = text(field(m, "name")) or "this[]"
            info.members[mname].append(Member("prop", mname, clean_type(text(field(m, "type")))))
        elif m.type in ("field_declaration", "event_field_declaration"):
            decl = next((c for c in m.named_children if c.type == "variable_declaration"), None)
            if decl is None:
                continue
            ftype = clean_type(text(field(decl, "type")))
            for d in decl.named_children:
                if d.type == "variable_declarator":
                    fname = text(field(d, "name"))
                    info.members[fname].append(Member("event" if m.type.startswith("event") else "field", fname, ftype))
        elif m.type == "event_declaration":
            mname = text(field(m, "name"))
            info.members[mname].append(Member("event", mname, clean_type(text(field(m, "type")))))
        elif m.type == "delegate_declaration":
            info.nested.add(text(field(m, "name")))


def is_closed(info, seen=None):
    """True when every base type is known, so a missing member is a real error."""
    seen = seen or set()
    if info.name in seen:
        return True
    seen.add(info.name)
    for base in info.bases:
        if base in KNOWN_EXTERNAL:
            continue
        other = TYPES.get(base)
        if other is None or base in AMBIGUOUS or not is_closed(other, seen):
            return False
    return True


def find_members(info, name, seen=None):
    seen = seen or set()
    if info.name in seen:
        return []
    seen.add(info.name)
    found = list(info.members.get(name, []))
    for base in info.bases:
        if base in KNOWN_EXTERNAL and name in KNOWN_EXTERNAL[base]:
            found.append(Member("external", name))
        other = TYPES.get(base)
        if other is not None and base not in AMBIGUOUS:
            found.extend(find_members(other, name, seen))
    return found


class Checker:
    def __init__(self, path, source):
        self.path, self.source = path, source
        self.cache = {}
        self.scope = {}
        self.klass = []
        self.lambda_types = {}
        self.binding = []
        self.local_functions = set()

    def report(self, node, message):
        line = node.start_point[0] + 1
        REPORTS.append(f"{self.path.relative_to(ROOT)}:{line}: {message}")

    # ---- declarations -------------------------------------------------------------------------

    def bind(self, name, type_):
        if name:
            self.scope[name] = clean_type(type_)

    def visit(self, node):
        t = node.type
        if t in TYPE_DECLS:
            info = TYPES.get(text(field(node, "name")))
            self.klass.append(info)
            saved = self.scope
            self.scope = {}
            for child in node.children:
                if child.type == "parameter_list":
                    for pname, ptype, _ in read_params(child)[0]:
                        self.bind(pname, ptype)
            for child in node.children:
                self.visit(child)
            self.scope = saved
            self.klass.pop()
            return
        if t in ("method_declaration", "constructor_declaration", "local_function_statement", "operator_declaration",
                 "conversion_operator_declaration", "destructor_declaration"):
            saved = dict(self.scope) if t == "local_function_statement" else None
            if t != "local_function_statement":
                self.scope = {}
                self.local_functions = {text(field(n, "name")) for n in self.descend(node) if n.type == "local_function_statement"}
            for pname, ptype, _ in read_params(field(node, "parameters"))[0]:
                self.bind(pname, ptype)
            for child in node.children:
                self.visit(child)
            if saved is not None:
                self.scope = saved
            return
        if t in ("property_declaration", "indexer_declaration", "accessor_declaration", "event_declaration"):
            if t != "accessor_declaration":
                self.scope = {}
                self.local_functions = set()
                ptype = clean_type(text(field(node, "type")))
                self.bind("value", ptype)
                if t == "indexer_declaration":
                    for pname, ptype2, _ in read_params(field(node, "parameters"))[0]:
                        self.bind(pname, ptype2)
            for child in node.children:
                self.visit(child)
            return
        if t == "field_declaration":
            self.scope = {}
            self.local_functions = set()
        if t == "variable_declaration":
            declared = field(node, "type")
            explicit = None if declared is None or declared.type == "implicit_type" else clean_type(text(declared))
            for d in node.named_children:
                if d.type != "variable_declarator":
                    continue
                init = [c for c in d.named_children if c.type not in ("identifier", "tuple_pattern", "bracketed_argument_list")]
                name_node = field(d, "name")
                inferred = None
                for expr in init:
                    self.visit(expr)
                    inferred = self.type_of(expr)
                    if explicit and expr.type == "implicit_object_creation_expression":
                        self.check_creation(expr, explicit)
                if name_node is not None:
                    self.bind(text(name_node), explicit or inferred)
                else:
                    for c in self.descend(d):
                        if c.type == "tuple_pattern":
                            for n in c.named_children:
                                if n.type == "identifier":
                                    self.bind(text(n), None)
            return
        if t == "foreach_statement":
            right = field(node, "right")
            self.visit(right)
            declared = field(node, "type")
            left = field(node, "left")
            if declared is not None and declared.type != "implicit_type":
                item = clean_type(text(declared))
            else:
                item = element_of(self.type_of(right))
            if left is not None and left.type == "identifier":
                self.bind(text(left), item)
            elif left is not None:
                for n in self.descend(left):
                    if n.type == "identifier":
                        self.bind(text(n), None)
            body = field(node, "body")
            if body is not None:
                self.visit(body)
            return
        if t == "catch_declaration":
            self.bind(text(field(node, "name")), text(field(node, "type")))
        if t == "declaration_pattern":
            self.bind(text(field(node, "name")), text(field(node, "type")))
        if t == "is_pattern_expression":
            pattern = field(node, "pattern")
            expr = field(node, "expression")
            self.visit(expr)
            if pattern is not None and pattern.type in ("recursive_pattern", "var_pattern"):
                names = [c for c in pattern.named_children if c.type == "identifier"]
                has_type = field(pattern, "type") is not None
                if names and not has_type:
                    self.bind(text(names[-1]), self.type_of(expr))
            if pattern is not None:
                self.visit(pattern)
            return
        if t == "recursive_pattern" and field(node, "type") is not None:
            names = [c for c in node.named_children if c.type == "identifier"]
            if names and names[-1] != field(node, "type"):
                self.bind(text(names[-1]), text(field(node, "type")))
        if t == "lambda_expression":
            saved = dict(self.scope)
            params = field(node, "parameters")
            inferred = self.lambda_types.get(node.id)
            if params is not None:
                if params.type == "implicit_parameter":
                    self.bind(text(params), inferred)
                else:
                    for p in params.named_children:
                        if p.type == "parameter":
                            ptype = field(p, "type")
                            self.bind(text(field(p, "name")), text(ptype) if ptype is not None else None)
                        elif p.type in ("implicit_parameter", "identifier"):
                            self.bind(text(p), None)
            body = field(node, "body")
            if body is not None:
                self.visit(body)
            self.scope = saved
            return
        if t in ("member_access_expression", "invocation_expression", "object_creation_expression",
                 "conditional_access_expression", "element_access_expression", "await_expression"):
            self.type_of(node)
        if t == "conditional_access_expression":
            self.visit(field(node, "condition"))
            self.binding.append(self.type_of(field(node, "condition")))
            for child in node.named_children[1:]:
                self.visit(child)
            self.binding.pop()
            return
        if t == "anonymous_object_creation_expression" or t == "query_expression":
            return
        for child in node.children:
            self.visit(child)

    def descend(self, node):
        stack = list(node.children)
        while stack:
            n = stack.pop()
            yield n
            stack.extend(n.children)

    # ---- expression typing --------------------------------------------------------------------

    def type_of(self, node):
        if node is None:
            return None
        key = (node.id, tuple(self.binding[-1:]))
        if key in self.cache:
            return self.cache[key]
        self.cache[key] = None
        result = self.compute(node)
        self.cache[key] = result
        return result

    def lookup_identifier(self, name):
        if name in self.scope:
            return self.scope[name]
        for info in reversed(self.klass):
            if info is None:
                continue
            members = find_members(info, name)
            if members:
                non_methods = [m for m in members if m.kind in ("prop", "field", "event", "enum")]
                return non_methods[0].type if non_methods else None
        if name in TYPES and name not in AMBIGUOUS:
            return "@" + name
        return None

    def member(self, node, receiver, name, call=None):
        """Type of receiver.name; reports when receiver is a fully known project type without that member."""
        if receiver is None:
            return None
        static = receiver.startswith("@")
        rtype = clean_type(receiver[1:] if static else receiver)
        info = project_type(rtype)
        if info is None:
            return self.external_member(rtype, name, call)
        STATS["resolved"] += 1
        if static and name in info.nested:
            return "@" + name
        members = find_members(info, name)
        if not members:
            if name in UNIVERSAL or not is_closed(info):
                return None
            if info.kind == "record" and name in ("EqualityContract",):
                return None
            self.report(node, f"'{info.name}' has no member '{name}'")
            return None
        if call is not None:
            methods = [m for m in members if m.kind == "method"]
            if not methods:
                return None  # delegate-typed property/field being invoked
            count, names = call
            if not any(m.accepts(count, names) for m in methods):
                shapes = " | ".join("(" + ", ".join(p[0] for p in m.params) + ")" for m in methods)
                self.report(node, f"'{info.name}.{name}' called with {count} argument(s){' ' + str(names) if names else ''}; declared: {shapes}")
            types = {m.type for m in methods}
            result = methods[0].type if len(types) == 1 else None
        else:
            values = [m for m in members if m.kind != "method"]
            if not values:
                return None  # method group
            result = values[0].type
        if result and info.type_params and any(re.search(rf"\b{re.escape(p)}\b", result) for p in info.type_params):
            return None
        return result

    def external_member(self, rtype, name, call):
        if rtype is None:
            return None
        head, args = split_generic(rtype)
        elem = element_of(rtype)
        if call is None:
            if name in ("Count", "Length"):
                return "int"
            if head in ("Dictionary", "IDictionary", "IReadOnlyDictionary", "ConcurrentDictionary") and len(args) == 2:
                if name == "Values":
                    return f"IEnumerable<{args[1]}>"
                if name == "Keys":
                    return f"IEnumerable<{args[0]}>"
            if name == "Value" and head in ("KeyValuePair",) and len(args) == 2:
                return args[1]
            if name == "Key" and head in ("KeyValuePair",) and len(args) == 2:
                return args[0]
            if name == "Result" and head in ("Task", "ValueTask") and len(args) == 1:
                return args[0]
            if name == "Value" and head in ("Nullable",) and len(args) == 1:
                return args[0]
            return None
        if name == "ConfigureAwait":
            return rtype
        if elem is not None:
            if name in SAME_SEQ:
                return f"IEnumerable<{elem}>"
            if name in ELEMENT:
                return elem
            if name in ("Any", "All", "Contains", "Exists", "Remove", "TrueForAll"):
                return "bool"
            if name in ("Count", "IndexOf", "FindIndex", "RemoveAll"):
                return "int"
        if head in ("Dictionary", "IDictionary", "IReadOnlyDictionary", "ConcurrentDictionary") and len(args) == 2:
            if name in ("GetValueOrDefault",):
                return args[1]
        return None

    def call_shape(self, args_node):
        count, names = 0, []
        if args_node is None:
            return 0, []
        for arg in args_node.named_children:
            if arg.type != "argument":
                continue
            count += 1
            name = field(arg, "name")
            if name is not None:
                names.append(text(name))
        return count, names

    def bind_lambdas(self, args_node, element):
        if args_node is None:
            return
        for arg in args_node.named_children:
            for c in arg.named_children:
                if c.type == "lambda_expression":
                    params = field(c, "parameters")
                    if params is not None and params.type == "implicit_parameter":
                        self.lambda_types[c.id] = element

    def bind_out_args(self, args_node, params):
        if args_node is None:
            return
        index = 0
        for arg in args_node.named_children:
            if arg.type != "argument":
                continue
            decl = next((c for c in arg.named_children if c.type == "declaration_expression"), None)
            if decl is not None:
                dtype = field(decl, "type")
                name = field(decl, "name")
                explicit = None if dtype is None or dtype.type == "implicit_type" else text(dtype)
                ptype = None
                arg_name = field(arg, "name")
                if params:
                    if arg_name is not None:
                        ptype = next((p[1] for p in params if p[0] == text(arg_name)), None)
                    elif index < len(params):
                        ptype = params[index][1]
                if name is not None:
                    self.bind(text(name), explicit or ptype)
            index += 1

    def check_creation(self, node, type_name):
        info = project_type(type_name)
        args = field(node, "arguments")
        if args is None:
            args = next((c for c in node.named_children if c.type == "argument_list"), None)
        init = field(node, "initializer")
        if init is None:
            init = next((c for c in node.named_children if c.type == "initializer_expression"), None)
        if info is None:
            self.bind_out_args(args, None)
            return
        STATS["constructions"] += 1
        if info.kind in ("class", "record", "struct") and args is not None:
            count, names = self.call_shape(args)
            ctors = info.ctors or [Member("ctor", info.name)]
            if not any(c.accepts(count, names) for c in ctors):
                shapes = " | ".join("(" + ", ".join(p[0] for p in c.params) + ")" for c in ctors)
                self.report(node, f"'new {info.name}' with {count} argument(s){' ' + str(names) if names else ''}; declared: {shapes}")
        if init is not None and is_closed(info):
            for assign in init.named_children:
                if assign.type == "assignment_expression":
                    left = field(assign, "left")
                    if left is not None and left.type == "identifier" and not find_members(info, text(left)):
                        self.report(assign, f"'{info.name}' has no member '{text(left)}' (object initializer)")

    def compute(self, node):
        t = node.type
        if t == "identifier":
            return self.lookup_identifier(text(node))
        if t == "this_expression":
            return self.klass[-1].name if self.klass and self.klass[-1] else None
        if t in ("parenthesized_expression", "checked_expression"):
            inner = [c for c in node.named_children]
            return self.type_of(inner[0]) if inner else None
        if t == "postfix_unary_expression":
            return self.type_of(node.named_children[0]) if text(node).endswith("!") and node.named_children else None
        if t in ("string_literal", "interpolated_string_expression", "verbatim_string_literal", "raw_string_literal"):
            return "string"
        if t == "integer_literal":
            return "int"
        if t == "boolean_literal":
            return "bool"
        if t == "cast_expression":
            return clean_type(text(field(node, "type")))
        if t == "as_expression":
            return clean_type(text(field(node, "right")))
        if t == "await_expression":
            inner = self.type_of(node.named_children[-1]) if node.named_children else None
            head, args = split_generic(inner)
            if head in ("Task", "ValueTask", "ConfiguredTaskAwaitable", "ConfiguredValueTaskAwaitable"):
                return args[0] if len(args) == 1 else None
            return None
        if t == "conditional_expression":
            return self.type_of(field(node, "consequence")) or self.type_of(field(node, "alternative"))
        if t == "binary_expression":
            op = text(field(node, "operator")) if field(node, "operator") is not None else ""
            if op == "??":
                return self.type_of(field(node, "left")) or self.type_of(field(node, "right"))
            return None
        if t == "assignment_expression":
            return self.type_of(field(node, "right"))
        if t == "object_creation_expression":
            type_name = clean_type(text(field(node, "type")))
            self.check_creation(node, type_name)
            return type_name
        if t == "member_access_expression":
            receiver_node = field(node, "expression")
            name_node = field(node, "name")
            if name_node is None or name_node.type != "identifier":
                self.type_of(receiver_node)
                return None
            return self.member(node, self.type_of(receiver_node), text(name_node))
        if t == "member_binding_expression":
            receiver = self.binding[-1] if self.binding else None
            name_node = field(node, "name")
            if name_node is None or name_node.type != "identifier":
                return None
            return self.member(node, receiver, text(name_node))
        if t == "conditional_access_expression":
            condition = field(node, "condition")
            self.binding.append(self.type_of(condition))
            try:
                rest = node.named_children[1:]
                return self.type_of(rest[-1]) if rest else None
            finally:
                self.binding.pop()
        if t == "element_access_expression":
            receiver = clean_type(self.type_of(field(node, "expression")))
            if receiver is None:
                return None
            if receiver == "string":
                return "char"
            head, args = split_generic(receiver)
            if head in ("Dictionary", "IDictionary", "IReadOnlyDictionary", "ConcurrentDictionary") and len(args) == 2:
                return args[1]
            return element_of(receiver)
        if t == "invocation_expression":
            function = field(node, "function")
            args = field(node, "arguments")
            shape = self.call_shape(args)
            if function is None:
                return None
            if function.type in ("member_access_expression", "member_binding_expression"):
                name_node = field(function, "name")
                if function.type == "member_access_expression":
                    receiver = self.type_of(field(function, "expression"))
                else:
                    receiver = self.binding[-1] if self.binding else None
                if name_node is None:
                    return None
                generic_call = name_node.type == "generic_name"
                name = text(name_node.named_children[0]) if generic_call and name_node.named_children else text(name_node)
                rtype = clean_type(receiver[1:] if receiver and receiver.startswith("@") else receiver)
                info = project_type(rtype)
                if info is not None:
                    methods = [m for m in find_members(info, name) if m.kind == "method"]
                    overload = next((m for m in methods if m.accepts(*shape)), methods[0] if methods else None)
                    self.bind_out_args(args, overload.params if overload else None)
                    if overload is not None:
                        for index, arg in enumerate(a for a in args.named_children if a.type == "argument") if args is not None else []:
                            if index < len(overload.params):
                                ptype = overload.params[index][1] or ""
                                head, pargs = split_generic(ptype)
                                lam = next((c for c in arg.named_children if c.type == "lambda_expression"), None)
                                if lam is not None and head in ("Action", "Func", "Predicate") and pargs:
                                    params = field(lam, "parameters")
                                    if params is not None and params.type == "implicit_parameter":
                                        self.lambda_types[lam.id] = pargs[0]
                    result = self.member(node, receiver, name, shape)
                    return None if generic_call else result
                elem = element_of(rtype)
                head, gargs = split_generic(rtype)
                if elem is not None and name in LAMBDA_ELEMENT:
                    self.bind_lambdas(args, elem)
                if head in ("Dictionary", "IDictionary", "IReadOnlyDictionary", "ConcurrentDictionary") and len(gargs) == 2:
                    if name in ("TryGetValue", "Remove", "TryRemove"):
                        self.bind_out_args(args, [("key", gargs[0], False), ("value", gargs[1], False)])
                        return "bool"
                    if name in LAMBDA_ELEMENT:
                        self.bind_lambdas(args, f"KeyValuePair<{gargs[0]},{gargs[1]}>")
                    self.bind_out_args(args, None)
                    return self.external_member(rtype, name, shape)
                self.bind_out_args(args, None)
                if elem is not None and name == "Select" and args is not None:
                    lam = next((c for a in args.named_children for c in a.named_children if c.type == "lambda_expression"), None)
                    if lam is not None and field(lam, "parameters") is not None and field(lam, "parameters").type == "implicit_parameter":
                        saved = dict(self.scope)
                        self.bind(text(field(lam, "parameters")), elem)
                        body = field(lam, "body")
                        inner = self.type_of(body) if body is not None and body.type != "block" else None
                        self.scope = saved
                        self.cache = {k: v for k, v in self.cache.items() if v is not None}
                        return f"IEnumerable<{clean_type(inner)}>" if inner and not inner.startswith("@") else None
                return self.external_member(rtype, name, shape)
            if function.type == "identifier":
                name = text(function)
                if name == "nameof":
                    return "string"
                if name in self.scope or name in self.local_functions:
                    self.bind_out_args(args, None)
                    return None
                for info in reversed(self.klass):
                    if info is None:
                        continue
                    members = find_members(info, name)
                    methods = [m for m in members if m.kind == "method"]
                    if methods:
                        STATS["resolved"] += 1
                        overload = next((m for m in methods if m.accepts(*shape)), None)
                        self.bind_out_args(args, (overload or methods[0]).params)
                        if overload is None:
                            shapes = " | ".join("(" + ", ".join(p[0] for p in m.params) + ")" for m in methods)
                            self.report(node, f"'{info.name}.{name}' called with {shape[0]} argument(s){' ' + str(shape[1]) if shape[1] else ''}; declared: {shapes}")
                            return None
                        return overload.type
                    if members:
                        break
                self.bind_out_args(args, None)
                return None
            self.bind_out_args(args, None)
            return None
        return None


def check_interfaces():
    for info in list(TYPES.values()):
        if info.kind not in ("class", "record", "struct") or info.name in AMBIGUOUS:
            continue
        for base in info.bases:
            iface = TYPES.get(base)
            if iface is None or iface.kind != "interface" or base in AMBIGUOUS:
                continue
            for name in iface.members:
                if not find_members(info, name) and not find_members(info, f"{base}.{name}"):
                    if is_closed(info):
                        REPORTS.append(f"{info.path.relative_to(ROOT)}: '{info.name}' does not implement '{base}.{name}'")
                    elif VERBOSE:
                        print(f"note: {info.name} relies on an external base for {base}.{name}")


def main():
    files = [p for p in sorted(ROOT.rglob("*.cs")) if not any(part in ("bin", "obj") for part in p.parts)]
    trees = {}
    for path in files:
        source = path.read_bytes()
        tree = PARSER.parse(source)
        trees[path] = (source, tree)
        stack = [tree.root_node]
        while stack:
            n = stack.pop()
            if n.type in TYPE_DECLS:
                collect_type(n, path)
            else:
                stack.extend(n.children)
    # Explicit interface implementations ("void IFoo.Bar()") are stored under their plain name too.
    for info in TYPES.values():
        for name in list(info.members):
            if "." in name:
                info.members[name.split(".")[-1]].extend(info.members[name])
    check_interfaces()
    for path, (source, tree) in trees.items():
        Checker(path, source).visit(tree.root_node)
    seen = []
    for r in REPORTS:
        if r not in seen:
            seen.append(r)
    for r in seen:
        print(r)
    print(f"{len(files)} files, {len(TYPES)} types ({len(AMBIGUOUS)} ambiguous names skipped), "
          f"{STATS['resolved']} member uses and {STATS['constructions']} constructions resolved, {len(seen)} finding(s)")
    if VERBOSE and AMBIGUOUS:
        print("ambiguous:", ", ".join(sorted(AMBIGUOUS)))
    return 1 if seen else 0


if __name__ == "__main__":
    sys.exit(main())
