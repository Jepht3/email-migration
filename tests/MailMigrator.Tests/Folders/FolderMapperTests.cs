using MailMigrator.Core.Folders;
using MailMigrator.Core.Imap;
using MailMigrator.Core.Models;

namespace MailMigrator.Tests.Folders;

public class FolderMapperTests
{
    private static RemoteFolder F(string path, SpecialFolder role = SpecialFolder.None, char sep = '/', bool isVirtual = false, bool canSelect = true) =>
        new(path, sep, role, canSelect, isVirtual);

    private static MailboxLayout Slash(params RemoteFolder[] folders) => new(folders, "", '/');

    private static FolderMapping Find(IReadOnlyList<FolderMapping> mappings, string source) => mappings.Single(m => m.SourcePath == source);

    private static readonly MigrationOptions Defaults = new();

    [Fact]
    public void Special_use_on_both_sides_wins_over_names()
    {
        var source = Slash(F("INBOX"), F("Sent", SpecialFolder.Sent));
        var dest = Slash(F("INBOX"), F("Envoyés", SpecialFolder.Sent), F("Sent"));

        var sent = Find(FolderMapper.Map(source, dest, Defaults), "Sent");

        Assert.Equal("Envoyés", sent.DestinationPath);
        Assert.Equal(MappingOrigin.SpecialUse, sent.Origin);
        Assert.Equal(SpecialFolder.Sent, sent.Role);
    }

    [Theory]
    [InlineData("Sent Items", "Envoyés")]
    [InlineData("Deleted Items", "Corbeille")]
    [InlineData("Brouillons", "Drafts")]
    [InlineData("Junk E-mail", "Spam")]
    public void Known_names_are_matched_without_special_use(string sourceName, string destName)
    {
        var source = Slash(F("INBOX"), F(sourceName));
        var dest = Slash(F("INBOX"), F(destName));
        var options = new MigrationOptions { IncludeTrash = true, IncludeJunk = true };

        var mapping = Find(FolderMapper.Map(source, dest, options), sourceName);

        Assert.Equal(destName, mapping.DestinationPath);
        Assert.Equal(MappingOrigin.KnownName, mapping.Origin);
    }

    [Fact]
    public void Auto_mapping_can_be_disabled()
    {
        var mapping = Find(FolderMapper.Map(Slash(F("Sent Items")), Slash(F("Envoyés")), new MigrationOptions { AutoMapFolders = false }), "Sent Items");

        Assert.Equal("Sent Items", mapping.DestinationPath);
        Assert.Equal(MappingOrigin.Created, mapping.Origin);
    }

    [Fact]
    public void Converts_dot_separator_and_inbox_namespace()
    {
        var source = new MailboxLayout([F("INBOX", sep: '.'), F("INBOX.Clients", sep: '.'), F("INBOX.Clients.Dupont", sep: '.')], "INBOX.", '.');
        var dest = Slash(F("INBOX"), F("Clients"));

        var mappings = FolderMapper.Map(source, dest, Defaults);

        Assert.Equal("INBOX", Find(mappings, "INBOX").DestinationPath);
        Assert.Equal(("Clients", MappingOrigin.SamePath), (Find(mappings, "INBOX.Clients").DestinationPath, Find(mappings, "INBOX.Clients").Origin));
        Assert.Equal(("Clients/Dupont", MappingOrigin.Created), (Find(mappings, "INBOX.Clients.Dupont").DestinationPath, Find(mappings, "INBOX.Clients.Dupont").Origin));
    }

    [Fact]
    public void Converts_slash_separator_to_dot_namespace()
    {
        var source = Slash(F("INBOX"), F("Archives/2023"));
        var dest = new MailboxLayout([F("INBOX", sep: '.')], "INBOX.", '.');

        Assert.Equal("INBOX.Archives.2023", Find(FolderMapper.Map(source, dest, Defaults), "Archives/2023").DestinationPath);
    }

    [Fact]
    public void Destination_separator_inside_a_name_is_neutralized()
    {
        var source = Slash(F("Factures.2024"));
        var dest = new MailboxLayout([F("INBOX", sep: '.')], "", '.');

        Assert.Equal("Factures_2024", Find(FolderMapper.Map(source, dest, Defaults), "Factures.2024").DestinationPath);
    }

    [Fact]
    public void Trash_and_junk_are_excluded_by_default_and_can_be_included()
    {
        var source = Slash(F("Trash", SpecialFolder.Trash), F("Spam"));
        var dest = Slash(F("Corbeille", SpecialFolder.Trash), F("Junk", SpecialFolder.Junk));

        var byDefault = FolderMapper.Map(source, dest, Defaults);
        var included = FolderMapper.Map(source, dest, new MigrationOptions { IncludeTrash = true, IncludeJunk = true });

        Assert.All(byDefault, m => Assert.False(m.Included));
        Assert.All(included, m => Assert.True(m.Included));
        Assert.Equal("Junk", Find(included, "Spam").DestinationPath);
    }

    [Fact]
    public void Excluded_folders_include_their_subfolders()
    {
        var source = Slash(F("Archives"), F("Archives/Ancien"), F("Archives/Ancien/2010"), F("Archives/Recent"));
        var options = new MigrationOptions { ExcludedFolders = ["archives/ancien"] };

        var mappings = FolderMapper.Map(source, Slash(), options);

        Assert.True(Find(mappings, "Archives").Included);
        Assert.False(Find(mappings, "Archives/Ancien").Included);
        Assert.False(Find(mappings, "Archives/Ancien/2010").Included);
        Assert.True(Find(mappings, "Archives/Recent").Included);
    }

    [Fact]
    public void Gmail_virtual_folders_and_non_selectable_parents_are_skipped()
    {
        var source = Slash(F("INBOX"), F("[Gmail]", canSelect: false), F("[Gmail]/All Mail", isVirtual: true), F("[Gmail]/Sent Mail", SpecialFolder.Sent));

        var mappings = FolderMapper.Map(source, Slash(F("INBOX"), F("Sent", SpecialFolder.Sent)), Defaults);

        Assert.DoesNotContain(mappings, m => m.SourcePath == "[Gmail]");
        Assert.False(Find(mappings, "[Gmail]/All Mail").Included);
        Assert.Equal("Sent", Find(mappings, "[Gmail]/Sent Mail").DestinationPath);
    }

    [Fact]
    public void Manual_override_has_priority()
    {
        var options = new MigrationOptions { FolderOverrides = new Dictionary<string, string> { ["Sent"] = "Archives/Envoyés 2023" } };

        var mapping = Find(FolderMapper.Map(Slash(F("Sent", SpecialFolder.Sent)), Slash(F("Envoyés", SpecialFolder.Sent)), options), "Sent");

        Assert.Equal(("Archives/Envoyés 2023", MappingOrigin.Manual), (mapping.DestinationPath, mapping.Origin));
    }

    [Fact]
    public void Special_folder_missing_at_destination_keeps_its_name()
    {
        var mapping = Find(FolderMapper.Map(Slash(F("Envoyés", SpecialFolder.Sent)), Slash(F("INBOX")), Defaults), "Envoyés");

        Assert.Equal(("Envoyés", MappingOrigin.Created), (mapping.DestinationPath, mapping.Origin));
    }
}
