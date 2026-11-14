"""Best-effort "is the namespace imported?" check for the C# sources (no .NET SDK needed).

For every type name used in a type position or as a static receiver, verifies that the declaring
namespace is reachable from the file: same or parent namespace, a `using` in the file, or a
`global using` of the project. Knows the project's own types plus a table of framework types.
Not a substitute for the compiler; the real build runs in CI.

Usage: python tools/check_usings.py [--unknown]
"""
import pathlib
import sys
from collections import defaultdict

import tree_sitter_c_sharp
from tree_sitter import Language, Parser

ROOT = pathlib.Path(__file__).resolve().parent.parent
PARSER = Parser(Language(tree_sitter_c_sharp.language()))
TYPE_DECLS = {"class_declaration", "interface_declaration", "record_declaration", "struct_declaration", "enum_declaration",
              "delegate_declaration"}

FRAMEWORK = {
    "System": "Action Func Predicate Exception ArgumentException ArgumentNullException ArgumentOutOfRangeException InvalidOperationException "
              "NotSupportedException OperationCanceledException ObjectDisposedException UnauthorizedAccessException TimeoutException "
              "FormatException OverflowException AggregateException Math Convert Environment Guid DateTime DateTimeOffset TimeSpan DayOfWeek "
              "DateTimeKind StringComparison StringComparer StringSplitOptions Array Console IDisposable IAsyncDisposable Uri UriKind Type "
              "Nullable Lazy Tuple ValueTuple Random GC IntPtr UIntPtr Enum Attribute FlagsAttribute SerializableAttribute Flags Version "
              "OperatingSystem EventArgs EventHandler IEquatable IComparable Activator AppDomain BitConverter Buffer Char String Int32 Int64 "
              "Double Boolean Object Delegate MidpointRounding STAThreadAttribute STAThread ObsoleteAttribute Obsolete Span ReadOnlySpan Memory "
              "ReadOnlyMemory Index Range PlatformNotSupportedException NotImplementedException NullReferenceException UriFormatException "
              "IServiceProvider IFormatProvider TimeZoneInfo StringComparison Half Single Byte Decimal UnhandledExceptionEventArgs "
              "DllNotFoundException EntryPointNotFoundException BadImageFormatException TimeOnly DateOnly",
    "System.Collections.Generic": "List Dictionary HashSet Queue Stack IEnumerable IList ICollection IDictionary IReadOnlyList "
                                  "IReadOnlyCollection IReadOnlyDictionary KeyValuePair IEqualityComparer IComparer SortedDictionary SortedSet "
                                  "LinkedList EqualityComparer Comparer KeyNotFoundException ISet IEnumerator SortedList IAsyncEnumerable",
    "System.IO": "File Directory Path FileInfo DirectoryInfo Stream FileStream MemoryStream StreamReader StreamWriter TextReader TextWriter "
                 "IOException FileNotFoundException DirectoryNotFoundException PathTooLongException FileMode FileAccess FileShare SearchOption "
                 "FileAttributes DriveInfo DriveType EnumerationOptions FileSystemInfo StringReader StringWriter BinaryReader BinaryWriter "
                 "EndOfStreamException InvalidDataException FileOptions SeekOrigin DriveNotFoundException MatchCasing",
    "System.Linq": "Enumerable IGrouping IOrderedEnumerable ILookup",
    "System.Threading": "CancellationToken CancellationTokenSource SemaphoreSlim Interlocked Thread Timer Monitor Mutex EventWaitHandle "
                        "EventResetMode WaitHandle ManualResetEventSlim Volatile ApartmentState SynchronizationContext Timeout "
                        "SemaphoreFullException ThreadPool AutoResetEvent ManualResetEvent Lock AbandonedMutexException "
                        "WaitHandleCannotBeOpenedException PeriodicTimer",
    "System.Threading.Tasks": "Task ValueTask TaskCompletionSource TaskCreationOptions TaskScheduler TaskCanceledException Parallel "
                              "TaskContinuationOptions UnobservedTaskExceptionEventArgs",
    "System.Text": "StringBuilder Encoding UTF8Encoding Rune",
    "System.Text.Json": "JsonSerializer JsonSerializerOptions JsonDocument JsonElement JsonException JsonValueKind JsonNamingPolicy "
                        "Utf8JsonWriter JsonWriterOptions JsonDocumentOptions",
    "System.Text.Json.Nodes": "JsonObject JsonArray JsonNode JsonValue",
    "System.Text.Json.Serialization": "JsonStringEnumConverter JsonIgnore JsonIgnoreAttribute JsonPropertyName JsonPropertyNameAttribute "
                                      "JsonIgnoreCondition JsonConverter",
    "System.Text.RegularExpressions": "Regex RegexOptions Match Group MatchCollection Capture RegexMatchTimeoutException",
    "System.Globalization": "CultureInfo NumberStyles DateTimeStyles CompareOptions",
    "System.Diagnostics": "Process ProcessStartInfo Stopwatch Debug ProcessWindowStyle PerformanceCounter Debugger FileVersionInfo",
    "System.Collections.Concurrent": "ConcurrentDictionary ConcurrentQueue ConcurrentBag BlockingCollection",
    "System.Collections.ObjectModel": "ObservableCollection ReadOnlyCollection Collection",
    "System.ComponentModel": "INotifyPropertyChanged PropertyChangedEventArgs PropertyChangedEventHandler CancelEventArgs Win32Exception "
                             "DescriptionAttribute",
    "System.Runtime.CompilerServices": "CallerMemberName CallerMemberNameAttribute",
    "System.Runtime.InteropServices": "DllImport DllImportAttribute Marshal StructLayout StructLayoutAttribute LayoutKind ComImport "
                                      "ComImportAttribute Guid GuidAttribute InterfaceType InterfaceTypeAttribute ComInterfaceType "
                                      "MarshalAs MarshalAsAttribute UnmanagedType PreserveSig PreserveSigAttribute COMException CharSet "
                                      "SafeHandle RuntimeInformation OSPlatform Out OutAttribute In InAttribute ClassInterface "
                                      "ClassInterfaceType ExternalException",
    "System.Net.Http": "HttpClient HttpRequestMessage HttpResponseMessage HttpMethod StringContent HttpRequestException HttpContent "
                       "HttpMessageHandler HttpCompletionOption ByteArrayContent",
    "System.Net": "HttpStatusCode WebUtility",
    "System.Net.NetworkInformation": "NetworkInterface NetworkChange OperationalStatus NetworkInterfaceType NetworkAvailabilityEventArgs",
    "System.Security.Cryptography": "ProtectedData DataProtectionScope SHA256 CryptographicException RandomNumberGenerator MD5 SHA1",
    "System.Security": "SecurityException",
    "System.IO.Compression": "ZipArchive ZipFile ZipArchiveMode ZipArchiveEntry",
    "System.Xml.Linq": "XDocument XElement XNamespace XName XAttribute XNode XText",
    "System.Xml": "XmlReader XmlReaderSettings XmlException DtdProcessing XmlNodeType",
    "System.Management": "ManagementObjectSearcher ManagementObject ManagementException ManagementScope ManagementBaseObject "
                         "ManagementClass ObjectQuery ManagementObjectCollection",
    "System.Speech.Recognition": "SpeechRecognitionEngine DictationGrammar RecognizeMode SpeechRecognizedEventArgs Grammar RecognizerInfo",
    "System.Speech.Synthesis": "SpeechSynthesizer Prompt SpeakCompletedEventArgs InstalledVoice VoiceInfo",
    "System.Drawing": "Bitmap Graphics Rectangle CopyPixelOperation Icon SystemIcons Font FontStyle Size Point Color Image",
    "System.Drawing.Imaging": "ImageFormat ImageCodecInfo EncoderParameters EncoderParameter PixelFormat Encoder",
    "System.Drawing.Drawing2D": "InterpolationMode SmoothingMode",
    "Microsoft.Win32": "Registry RegistryKey RegistryValueKind RegistryHive RegistryView",
    "Microsoft.Data.Sqlite": "SqliteConnection SqliteCommand SqliteDataReader SqliteException SqliteConnectionStringBuilder SqliteTransaction "
                             "SqliteParameter SqliteOpenMode SqliteCacheMode",
    "Microsoft.Extensions.Logging": "ILogger ILoggerFactory ILoggerProvider LogLevel EventId LoggerFactory LoggerExtensions",
    "Microsoft.Extensions.Logging.Abstractions": "NullLogger NullLoggerFactory",
    "Microsoft.Extensions.DependencyInjection": "ServiceCollection ServiceProvider IServiceCollection ActivatorUtilities",
    "UglyToad.PdfPig": "PdfDocument ParsingOptions",
    "UglyToad.PdfPig.Content": "Page",
    "System.Windows": "Application Window Visibility MessageBox MessageBoxButton MessageBoxImage MessageBoxResult RoutedEventArgs "
                      "StartupEventArgs ExitEventArgs WindowState SystemParameters DependencyObject FrameworkElement Thickness "
                      "SessionEndingCancelEventArgs ResourceDictionary DependencyProperty UIElement Rect",
    "System.Windows.Input": "ICommand CommandManager Key KeyEventArgs Keyboard ModifierKeys MouseButtonEventArgs Mouse",
    "System.Windows.Data": "IValueConverter Binding",
    "System.Windows.Threading": "Dispatcher DispatcherPriority DispatcherUnhandledExceptionEventArgs DispatcherTimer",
    "System.Windows.Interop": "WindowInteropHelper HwndSource",
    "System.Windows.Controls": "TextBox Button RadioButton ScrollViewer PasswordBox ItemsControl",
    "Xunit": "Fact FactAttribute Theory TheoryAttribute InlineData InlineDataAttribute Assert",
}
FRAMEWORK_TYPES = defaultdict(set)
for namespace, names in FRAMEWORK.items():
    for name in names.split():
        FRAMEWORK_TYPES[name].add(namespace)

TYPE_PARENT_FIELDS = {
    ("object_creation_expression", "type"), ("variable_declaration", "type"), ("parameter", "type"), ("cast_expression", "type"),
    ("method_declaration", "returns"), ("property_declaration", "type"), ("declaration_pattern", "type"), ("as_expression", "right"),
    ("typeof_expression", "type"), ("catch_declaration", "type"), ("foreach_statement", "type"), ("nullable_type", "type"),
    ("array_type", "type"), ("local_function_statement", "type"), ("declaration_expression", "type"), ("indexer_declaration", "type"),
    ("recursive_pattern", "type"), ("is_expression", "right"), ("default_expression", "type"), ("delegate_declaration", "returns"),
    ("event_declaration", "type"), ("type_pattern", "type"), ("constant_pattern", None),
}


def text(node):
    return node.text.decode("utf-8", "replace") if node is not None else ""


def namespaces_of(root):
    found = []
    for n in root.children:
        if n.type in ("file_scoped_namespace_declaration", "namespace_declaration"):
            found.append(text(n.child_by_field_name("name")))
    return found


def usings_of(root):
    plain, global_ = set(), set()
    stack = [root]
    while stack:
        n = stack.pop()
        if n.type == "using_directive":
            is_global = any(text(c) == "global" for c in n.children)
            is_static = any(text(c) == "static" for c in n.children)
            alias = n.child_by_field_name("name") is not None and any(text(c) == "=" for c in n.children)
            names = [c for c in n.named_children if c.type in ("qualified_name", "identifier")]
            if names and not alias and not is_static:
                (global_ if is_global else plain).add(text(names[-1]))
        elif n.type in ("compilation_unit", "namespace_declaration", "file_scoped_namespace_declaration", "declaration_list"):
            stack.extend(n.children)
    return plain, global_


def project_of(path):
    current = path.parent
    while current != ROOT.parent:
        if any(current.glob("*.csproj")):
            return current
        current = current.parent
    return None


def main():
    show_unknown = "--unknown" in sys.argv
    files = [p for p in sorted(ROOT.rglob("*.cs")) if not any(part in ("bin", "obj") for part in p.parts)]
    parsed, declared, project_globals = {}, defaultdict(set), defaultdict(set)
    type_params = defaultdict(set)
    for path in files:
        tree = PARSER.parse(path.read_bytes())
        parsed[path] = tree
        spaces = namespaces_of(tree.root_node)
        _, global_ = usings_of(tree.root_node)
        project_globals[project_of(path)] |= global_
        stack = [tree.root_node]
        while stack:
            n = stack.pop()
            if n.type in TYPE_DECLS:
                declared[text(n.child_by_field_name("name"))].add(spaces[0] if spaces else "")
            if n.type == "type_parameter":
                type_params[path].add(text(n).split()[-1])
            stack.extend(n.children)

    findings, unknown = [], defaultdict(int)
    for path, tree in parsed.items():
        spaces = namespaces_of(tree.root_node)
        own = spaces[0] if spaces else ""
        reachable = {""}
        parts = own.split(".")
        for i in range(1, len(parts) + 1):
            reachable.add(".".join(parts[:i]))
        plain, _ = usings_of(tree.root_node)
        reachable |= plain | project_globals[project_of(path)]

        seen = set()
        stack = [tree.root_node]
        while stack:
            n = stack.pop()
            stack.extend(n.children)
            if n.type != "identifier":
                continue
            parent = n.parent
            name = text(n)
            if not name[:1].isupper() or name in type_params[path]:
                continue
            in_type_position = False
            static_receiver = False
            if parent is not None:
                for i, c in enumerate(parent.children):
                    if c.id == n.id:
                        fname = parent.field_name_for_child(i)
                        if (parent.type, fname) in TYPE_PARENT_FIELDS:
                            in_type_position = True
                        if parent.type in ("type_argument_list", "base_list", "attribute"):
                            in_type_position = True
                        if parent.type == "generic_name" and i == 0:
                            gp = parent.parent
                            # Generic method calls (Foo.Bar<T>()) are not type references.
                            if gp is not None and gp.type not in ("member_access_expression", "invocation_expression",
                                                                  "member_binding_expression"):
                                in_type_position = True
                        if parent.type == "member_access_expression" and fname == "expression":
                            static_receiver = True
                        if parent.type == "qualified_name":
                            in_type_position = False
            if not (in_type_position or static_receiver):
                continue
            homes = set(declared.get(name, set())) | FRAMEWORK_TYPES.get(name, set())
            if not homes:
                if in_type_position:
                    unknown[name] += 1
                continue
            if static_receiver and not in_type_position and name not in declared and name not in FRAMEWORK_TYPES:
                continue
            if homes & reachable:
                continue
            if static_receiver and not in_type_position:
                # Could equally be a property or field with that name; only flag when nothing else explains it.
                source = path.read_text(encoding="utf-8")
                if f" {name} " in source.replace(f" {name} =", f" {name} ").replace(f" {name};", f" {name} ") and (
                        f" {name} {{" in source or f" {name};" in source or f" {name} =" in source or f" {name}," in source):
                    continue
            key = (name, n.start_point[0])
            if key in seen:
                continue
            seen.add(key)
            findings.append(f"{path.relative_to(ROOT)}:{n.start_point[0] + 1}: '{name}' needs one of: {', '.join(sorted(homes))}")

    for f in findings:
        print(f)
    if show_unknown:
        for name, count in sorted(unknown.items()):
            print(f"unknown type name (not in table): {name} x{count}")
    print(f"{len(files)} files, {len(findings)} finding(s), {len(unknown)} type name(s) outside the lookup table")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
