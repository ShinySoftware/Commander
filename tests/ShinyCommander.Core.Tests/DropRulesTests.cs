using ShinyCommander.Core.Operations;

namespace ShinyCommander.Core.Tests;

public class DropRulesTests
{
    static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";
    static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    const DragOperations Both = DragOperations.Copy | DragOperations.Move;

    [Fact]
    public void Same_volume_moves()
        => DropRules.Choose([P("tmp", "a", "file.txt")], P("tmp", "b"), Both).ShouldBe(DragOperations.Move);

    [Fact]
    public void Archive_or_server_target_copies()
        => DropRules.Choose([P("tmp", "a", "file.txt")], P("tmp", "b.zip"), Both, targetOnDisk: false).ShouldBe(DragOperations.Copy);

    [Theory]
    [InlineData(DragOperations.Copy)]   // ⌥ held, or a source that only lets go of copies
    [InlineData(DragOperations.Move)]   // ⌘ held
    public void A_single_offered_operation_wins(DragOperations allowed)
        => DropRules.Choose([P("tmp", "a", "file.txt")], P("tmp", "b"), allowed).ShouldBe(allowed);

    [Fact]
    public void Nothing_offered_is_refused()
        => DropRules.Choose([P("tmp", "a", "file.txt")], P("tmp", "b"), DragOperations.None).ShouldBe(DragOperations.None);

    [Fact]
    public void Dropping_back_into_the_same_folder_is_refused()
        => DropRules.Choose([P("tmp", "a", "x.txt"), P("tmp", "a", "y.txt")], P("tmp", "a"), Both).ShouldBe(DragOperations.None);

    [Fact]
    public void Some_from_elsewhere_still_goes()
        => DropRules.CanDropInto([P("tmp", "a", "x.txt"), P("tmp", "b", "y.txt")], P("tmp", "a")).ShouldBeTrue();

    [Fact]
    public void A_folder_into_itself_is_refused()
        => DropRules.CanDropInto([P("tmp", "a")], P("tmp", "a")).ShouldBeFalse();

    [Fact]
    public void A_folder_into_its_own_subfolder_is_refused()
        => DropRules.CanDropInto([P("tmp", "a")], P("tmp", "a", "sub")).ShouldBeFalse();

    [Fact]
    public void A_sibling_with_a_longer_name_is_not_inside()
        => DropRules.CanDropInto([P("tmp", "a")], P("tmp", "ab")).ShouldBeTrue();

    [Fact]
    public void Volume_is_the_longest_mount_point()
    {
        if (OperatingSystem.IsWindows())
            return;

        string[] mounts = ["/", "/Volumes/USB", "/Volumes/USB2"];
        DropRules.VolumeOf("/Users/me/file.txt", mounts).ShouldBe("/");
        DropRules.VolumeOf("/Volumes/USB/photos/a.jpg", mounts).ShouldBe("/Volumes/USB");
        DropRules.VolumeOf("/Volumes/USB2", mounts).ShouldBe("/Volumes/USB2");
        DropRules.VolumeOf("/Volumes/USB22/x", mounts).ShouldBe("/");
    }
}
