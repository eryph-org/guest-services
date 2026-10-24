using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;

namespace Eryph.GuestServices.CloudConfig.Yaml.Tests;

public sealed class PackagesUmbrellaYamlTests
{
    private const string Yaml = """
        #cloud-config
        packages:
          - 7zip.7zip
          - [Git.Git, 2.46.0]
          - winget:
              - Microsoft.PowerShell
              - id: Foo.Bar
                version: 1.2.3
            chocolatey:
              - nodejs
          - apt:
              - htop
        """;

    [Fact]
    public void Parses_the_three_cloud_init_entry_shapes()
    {
        var config = CloudConfigYamlSerializer.Deserialize(Yaml);

        config.Packages.Should().NotBeNull();
        config.Packages!.Should().HaveCount(4);

        // Bare name.
        config.Packages[0].Name.Should().Be("7zip.7zip");
        config.Packages[0].Version.Should().BeNull();
        config.Packages[0].Managers.Should().BeNull();

        // [name, version] pair.
        config.Packages[1].Name.Should().Be("Git.Git");
        config.Packages[1].Version.Should().Be("2.46.0");

        // Manager-keyed mapping with two managers.
        var managers = config.Packages[2].Managers;
        managers.Should().NotBeNull();
        managers!.Should().ContainKeys("winget", "chocolatey");
        managers["winget"].Should().HaveCount(2);
        managers["winget"][0].Name.Should().Be("Microsoft.PowerShell");
        managers["winget"][1].Name.Should().Be("Foo.Bar");
        managers["winget"][1].Version.Should().Be("1.2.3");
        managers["chocolatey"].Should().ContainSingle();
        managers["chocolatey"][0].Name.Should().Be("nodejs");

        // Linux manager is parsed too (routing ignores it on Windows).
        config.Packages[3].Managers.Should().ContainKey("apt");
    }

    [Fact]
    public void Round_trips_through_serialize_deserialize()
    {
        var once = CloudConfigYamlSerializer.Deserialize(Yaml);
        var yaml = CloudConfigYamlSerializer.Serialize(once);
        var twice = CloudConfigYamlSerializer.Deserialize(yaml);

        twice.Packages.Should().BeEquivalentTo(once.Packages);
    }

    [Fact]
    public void Tolerates_edge_shapes_without_desync()
    {
        // Null-like scalar, a scalar manager value, a non-scalar item sub-value,
        // and case-different manager keys in one mapping.
        const string yaml = """
            #cloud-config
            packages:
              - ~
              - winget: Microsoft.PowerShell
              - winget:
                  - id: Foo.Bar
                    params: [a, b]
                Winget:
                  - Baz.Qux
            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);
        config.Packages!.Should().HaveCount(3);

        // 1. `- ~` carries no name (not the literal "~").
        config.Packages[0].Name.Should().BeNull();

        // 2. Scalar manager value becomes a single item.
        config.Packages[1].Managers!["winget"].Should().ContainSingle(p => p.Name == "Microsoft.PowerShell");

        // 3. The non-scalar `params:` is ignored; case-different keys merge.
        var winget = config.Packages[2].Managers!["winget"];
        winget.Select(p => p.Name).Should().BeEquivalentTo("Foo.Bar", "Baz.Qux");
        winget.Single(p => p.Name == "Foo.Bar").Version.Should().BeNull();
    }

    [Fact]
    public void Manager_item_name_version_pair_is_accepted()
    {
        // cloud-init allows [name, version] pairs as manager items, including
        // for Linux managers that Windows ignores — this must parse, not reject
        // the whole document.
        const string yaml = """
            #cloud-config
            packages:
              - winget:
                  - [Git.Git, 2.46.0]
              - apt:
                  - [curl, '7.88.1-10']
            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);

        var winget = config.Packages![0].Managers!["winget"][0];
        winget.Name.Should().Be("Git.Git");
        winget.Version.Should().Be("2.46.0");
        var apt = config.Packages[1].Managers!["apt"][0];
        apt.Name.Should().Be("curl");
        apt.Version.Should().Be("7.88.1-10");
    }

    [Fact]
    public void Null_mapping_values_become_unset_not_literal_null()
    {
        const string yaml = """
            #cloud-config
            packages:
              - winget:
                  - id: Git.Git
                    version: null
                  - id: ~
            """;

        var config = CloudConfigYamlSerializer.Deserialize(yaml);
        var items = config.Packages![0].Managers!["winget"];

        items[0].Name.Should().Be("Git.Git");
        items[0].Version.Should().BeNull(); // not the string "null"
        items[1].Name.Should().Be("");      // ~ is unset, not literal "~"
    }

    [Fact]
    public void Numeric_version_is_quoted_on_write_for_pyyaml_fidelity()
    {
        // A plain 20.11 would be read back by PyYAML (cloud-init) as a float, so
        // the serializer must quote it.
        var config = new CloudConfig
        {
            Packages =
            [
                new PackageEntry
                {
                    Managers = new Dictionary<string, IReadOnlyList<ManagerPackage>>
                    {
                        ["winget"] = [new ManagerPackage { Name = "Foo", Version = "20.11" }],
                    },
                },
            ],
        };

        var yaml = CloudConfigYamlSerializer.Serialize(config);

        yaml.Should().Contain("\"20.11\"");
    }
}
