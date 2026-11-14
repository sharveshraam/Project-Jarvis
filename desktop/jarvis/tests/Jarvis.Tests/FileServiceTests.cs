using Jarvis.Core.Files;

namespace Jarvis.Tests;

/// <summary>File safety guarantees, exercised against a real temporary folder.</summary>
public class FileServiceTests : IDisposable
{
    private readonly TempFolder _temp = new TempFolder();
    private readonly InMemoryJournal _journal = new InMemoryJournal();
    private readonly FileService _files;

    public FileServiceTests() => _files = new FileService(_journal);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Organising_is_a_preview_until_applied()
    {
        _temp.File("photo.jpg");
        _temp.File("thesis.pdf");
        _temp.File("movie.mkv.crdownload");
        _temp.File(Path.Combine("Existing", "nested.txt"));

        var plan = _files.PlanOrganise(_temp.Root);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, m => m.Destination == Path.Combine(_temp.Root, "Images", "photo.jpg"));
        Assert.Contains(plan, m => m.Destination == Path.Combine(_temp.Root, "Documents", "thesis.pdf"));
        Assert.True(File.Exists(Path.Combine(_temp.Root, "photo.jpg")));
        Assert.False(Directory.Exists(Path.Combine(_temp.Root, "Images")));
    }

    [Fact]
    public void Organising_moves_files_and_can_be_undone_completely()
    {
        var photo = _temp.File("photo.jpg", "image-bytes");
        var paper = _temp.File("thesis.pdf", "pdf-bytes");

        var outcome = _files.ApplyMoves(_files.PlanOrganise(_temp.Root), CancellationToken.None);

        Assert.Equal(2, outcome.Moved);
        Assert.Empty(outcome.Errors);
        Assert.False(File.Exists(photo));
        Assert.True(File.Exists(Path.Combine(_temp.Root, "Images", "photo.jpg")));
        Assert.True(File.Exists(Path.Combine(_temp.Root, "Documents", "thesis.pdf")));
        Assert.Equal(outcome.BatchId, _journal.LatestBatchId());

        var undo = _files.UndoBatch(outcome.BatchId);

        Assert.Equal(2, undo.Moved);
        Assert.Empty(undo.Errors);
        Assert.Equal("image-bytes", File.ReadAllText(photo));
        Assert.Equal("pdf-bytes", File.ReadAllText(paper));
        Assert.False(Directory.Exists(Path.Combine(_temp.Root, "Images")));
        Assert.False(Directory.Exists(Path.Combine(_temp.Root, "Documents")));
        Assert.Contains(outcome.BatchId, _journal.Undone);
    }

    [Fact]
    public void Organising_never_overwrites_a_file_that_is_already_in_the_category_folder()
    {
        _temp.File(Path.Combine("Documents", "notes.txt"), "old");
        _temp.File("notes.txt", "new");

        var outcome = _files.ApplyMoves(_files.PlanOrganise(_temp.Root), CancellationToken.None);

        Assert.Equal(1, outcome.Moved);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_temp.Root, "Documents", "notes.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_temp.Root, "Documents", "notes (2).txt")));
    }

    [Fact]
    public void Move_refuses_to_overwrite_unless_asked()
    {
        var source = _temp.File("a.txt", "source");
        var target = _temp.File("b.txt", "target");

        Assert.Throws<IOException>(() => _files.Move(source, target, overwrite: false));
        Assert.Equal("target", File.ReadAllText(target));
        Assert.True(File.Exists(source));

        _files.Move(source, target, overwrite: true);
        Assert.Equal("source", File.ReadAllText(target));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Moving_into_an_existing_folder_keeps_the_file_name()
    {
        var source = _temp.File("report.docx", "r");
        var folder = _temp.Sub("Archive");

        var result = _files.Move(source, folder, overwrite: false);

        Assert.Equal(Path.Combine(folder, "report.docx"), result);
        Assert.True(File.Exists(result));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Copy_refuses_to_overwrite_unless_asked()
    {
        var source = _temp.File("a.txt", "source");
        var target = _temp.File("b.txt", "target");

        Assert.Throws<IOException>(() => _files.Copy(source, target, overwrite: false));
        Assert.Equal("target", File.ReadAllText(target));

        _files.Copy(source, target, overwrite: true);
        Assert.Equal("source", File.ReadAllText(target));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void A_folder_cannot_be_moved_into_itself()
    {
        var folder = _temp.Sub("Projects");
        _temp.File(Path.Combine("Projects", "a.txt"));

        Assert.Throws<IOException>(() => _files.Move(folder, Path.Combine(folder, "Inner"), overwrite: false));
        Assert.True(File.Exists(Path.Combine(folder, "a.txt")));
    }

    [Fact]
    public void Duplicates_are_found_by_content_not_by_name()
    {
        _temp.File("one.txt", "same content here");
        _temp.File(Path.Combine("sub", "two.txt"), "same content here");
        _temp.File("three.txt", "different content!");
        _temp.File("four.txt", "same length, other");

        var topLevelOnly = _files.FindDuplicates(_temp.Root, recursive: false, CancellationToken.None);
        Assert.Empty(topLevelOnly);

        var groups = _files.FindDuplicates(_temp.Root, recursive: true, CancellationToken.None);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Paths.Count);
        Assert.Contains(groups[0].Paths, p => p.EndsWith("one.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups[0].Paths, p => p.EndsWith("two.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Old_files_are_reported_but_not_touched()
    {
        var old = _temp.File("old.log", "x");
        var fresh = _temp.File("fresh.log", "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-400));

        var found = _files.FindOldFiles(_temp.Root, 180, 50, CancellationToken.None);

        Assert.Single(found);
        Assert.Equal(old, found[0].Path);
        Assert.True(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Folders_can_be_created_and_items_renamed()
    {
        var created = _files.CreateFolder(Path.Combine(_temp.Root, "New", "Deep"));
        Assert.True(Directory.Exists(created));

        var file = _temp.File("draft.txt", "d");
        var renamed = _files.Rename(file, "final.txt");
        Assert.Equal(Path.Combine(_temp.Root, "final.txt"), renamed);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(file));

        Assert.Throws<ArgumentException>(() => _files.Rename(renamed, @"..\escape.txt"));
        Assert.True(File.Exists(renamed));
    }

    [Fact]
    public void File_details_and_listing_describe_what_exists()
    {
        var file = _temp.File("notes.md", "hello");
        _temp.Sub("Folder");

        var info = _files.GetInfo(file);
        Assert.False(info.IsDirectory);
        Assert.Equal(5, info.SizeBytes);
        Assert.Equal("Documents", info.Kind);

        var listing = _files.List(_temp.Root, 10);
        Assert.Equal(2, listing.Count);
        Assert.Contains(listing, f => f.IsDirectory);

        Assert.Throws<FileNotFoundException>(() => _files.GetInfo(Path.Combine(_temp.Root, "missing.txt")));
    }

    [Fact]
    public void Text_reads_are_capped()
    {
        var file = _temp.File("long.txt", new string('a', 5000));
        Assert.Equal(100, _files.ReadText(file, 100).Length);
    }
}
