using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.OpenStackMetadataSim;

namespace Eryph.GuestServices.OpenStackMetadataSim.Tests;

public sealed class MetadataResponderTests : IDisposable
{
    private readonly string _root;

    public MetadataResponderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-sim-" + Guid.NewGuid().ToString("N"));
        var versionDir = Path.Combine(_root, "openstack", "2018-08-27");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "meta_data.json"), "{\"uuid\":\"sim-1\"}");
        File.WriteAllText(Path.Combine(versionDir, "user_data"), "#cloud-config\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Openstack_root_returns_the_version_listing()
    {
        var responder = new MetadataResponder(_root);

        var r = responder.Respond("/openstack");

        r.StatusCode.Should().Be(200);
        Encoding.UTF8.GetString(r.Body).Should().Be("2018-08-27");
    }

    [Fact]
    public void Version_listing_includes_every_present_version_dir_newline_separated()
    {
        Directory.CreateDirectory(Path.Combine(_root, "openstack", "latest"));
        var responder = new MetadataResponder(_root);

        var r = responder.Respond("/openstack");

        Encoding.UTF8.GetString(r.Body).Split('\n')
            .Should().BeEquivalentTo("2018-08-27", "latest");
    }

    [Fact]
    public void Serves_meta_data_json_with_json_content_type()
    {
        var responder = new MetadataResponder(_root);

        var r = responder.Respond("/openstack/2018-08-27/meta_data.json");

        r.StatusCode.Should().Be(200);
        r.ContentType.Should().Be("application/json");
        Encoding.UTF8.GetString(r.Body).Should().Contain("\"uuid\":\"sim-1\"");
    }

    [Fact]
    public void Serves_user_data_as_octet_stream()
    {
        var responder = new MetadataResponder(_root);

        var r = responder.Respond("/openstack/2018-08-27/user_data");

        r.StatusCode.Should().Be(200);
        r.ContentType.Should().Be("application/octet-stream");
        Encoding.UTF8.GetString(r.Body).Should().StartWith("#cloud-config");
    }

    [Fact]
    public void Absent_file_returns_404()
    {
        var responder = new MetadataResponder(_root);

        responder.Respond("/openstack/2018-08-27/vendor_data.json").StatusCode.Should().Be(404);
    }

    [Fact]
    public void Path_traversal_is_refused()
    {
        var responder = new MetadataResponder(_root);

        // Even if the parent dir holds a file, the traversal attempt must 404.
        responder.Respond("/openstack/../../etc/passwd").StatusCode.Should().Be(404);
    }
}
