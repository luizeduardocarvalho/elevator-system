using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Elevator.Domain;
using Elevator.EventLog;
using Elevator.Metrics;
using Elevator.Policy;
using Elevator.Runtime;

namespace Elevator.Architecture.Tests;

/// <summary>
/// The lab depends on the system, never the reverse (ADR-019). Checked three ways, because each one alone can be
/// fooled: the declared project graph (an unused reference is still a dependency), the compiled assembly references
/// (what actually loads), and namespaces (code moved into a system project but still in a lab namespace, or the
/// reverse).
/// </summary>
public class LayeringTests
{
    /// <summary>The system: what would ship. Anchored by one type each, so the assemblies under test are loaded.</summary>
    private static readonly Dictionary<string, Assembly> SystemProjects = new[]
    {
        typeof(ElevatorType).Assembly,
        typeof(LookScheduling).Assembly,
        typeof(JsonlEventLog).Assembly,
        typeof(ElevatorSystem).Assembly,
        typeof(ReportBuilder).Assembly,
    }.ToDictionary(a => a.GetName().Name!);

    /// <summary>The lab: simulation, experiments and tools. Project name → (assembly name, root namespace).</summary>
    private static readonly Dictionary<string, (string Assembly, string Namespace)> LabProjects = new()
    {
        ["Elevator.Simulation"] = ("Elevator.Simulation", "Elevator.Simulation"),
        ["Elevator.Lab"] = ("Elevator.Lab", "Elevator.Lab"),
        ["Elevator.Tui"] = ("Elevator.Tui", "Elevator.Tui"),
        ["Elevator.Cli"] = ("elevator", "Elevator.Cli"),
    };

    public static TheoryData<string> SystemProjectNames => [.. SystemProjects.Keys];

    [Fact]
    public void Every_project_is_classified_as_system_or_lab()
    {
        var projects = Directory.GetDirectories(Path.Combine(Repo.Root, "src")).Select(Path.GetFileName).Order();

        Assert.Equal(SystemProjects.Keys.Concat(LabProjects.Keys).Order(), projects);
    }

    [Theory]
    [MemberData(nameof(SystemProjectNames))]
    public void Declared_project_references_never_reach_the_lab(string project)
    {
        var reached = ProjectClosure(project);

        Assert.Empty(reached.Intersect(LabProjects.Keys));
    }

    [Theory]
    [MemberData(nameof(SystemProjectNames))]
    public void Compiled_assembly_references_never_name_the_lab(string project)
    {
        var references = SystemProjects[project].GetReferencedAssemblies().Select(a => a.Name);

        Assert.Empty(references.Intersect(LabProjects.Values.Select(l => l.Assembly)));
    }

    [Theory]
    [MemberData(nameof(SystemProjectNames))]
    public void Types_live_under_their_own_namespace_and_never_reference_a_lab_namespace(string project)
    {
        var assembly = SystemProjects[project];
        var misplaced = assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && ns.StartsWith("Elevator", StringComparison.Ordinal))
            .Where(t => t.Namespace != project && !t.Namespace!.StartsWith(project + ".", StringComparison.Ordinal))
            .Select(t => t.FullName);
        Assert.Empty(misplaced);

        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var metadata = pe.GetMetadataReader();
        var labTypes = metadata.TypeReferences
            .Select(h => metadata.GetTypeReference(h))
            .Select(r => $"{metadata.GetString(r.Namespace)}.{metadata.GetString(r.Name)}")
            .Where(name => LabProjects.Values.Any(l => name.StartsWith(l.Namespace + ".", StringComparison.Ordinal)));
        Assert.Empty(labTypes);
    }

    /// <summary>Every project <paramref name="project"/> reaches through ProjectReference, transitively.</summary>
    private static HashSet<string> ProjectClosure(string project)
    {
        var seen = new HashSet<string>();
        var pending = new Stack<string>([project]);
        while (pending.TryPop(out var current))
        {
            var csproj = Path.Combine(Repo.Root, "src", current, current + ".csproj");
            foreach (var reference in XDocument.Load(csproj).Descendants("ProjectReference"))
            {
                var name = Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/'));
                if (seen.Add(name)) pending.Push(name);
            }
        }
        return seen;
    }
}
