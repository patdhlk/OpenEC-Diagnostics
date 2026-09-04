// src/OpenEC.Monitor/Eni/EniConfiguration.cs
using System.Xml.Linq;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Eni;

/// <summary>Parsed EtherCAT Network Information (ENI) file. Namespace-agnostic and
/// tolerant: missing sections leave the corresponding lists empty.</summary>
public sealed class EniConfiguration
{
    public required IReadOnlyList<EniSlave> Slaves { get; init; }
    public required IReadOnlyList<EniCyclicCommand> CyclicCommands { get; init; }
    public required IReadOnlyList<EniVariable> Variables { get; init; }
    public int? CycleTimeMicroseconds { get; init; }

    public static EniConfiguration Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static EniConfiguration Load(Stream stream)
    {
        var doc = XDocument.Load(stream);
        return new EniConfiguration
        {
            Slaves = ParseSlaves(doc),
            CyclicCommands = ParseCyclic(doc),
            Variables = ParseVariables(doc),
            CycleTimeMicroseconds = (int?)EniXmlValues.ParseNumber(
                Local(doc.Root, "Config", "Cyclic", "CycleTime")?.Value),
        };
    }

    private static IEnumerable<XElement> LocalDescendants(XContainer? node, string name) =>
        node?.Descendants().Where(e => e.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

    private static XElement? Local(XContainer? node, params string[] path)
    {
        var current = node as XElement ?? (node as XDocument)?.Root;
        foreach (var name in path)
        {
            current = current?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (current is null) return null;
        }
        return current;
    }

    private static string? Text(XContainer? parent, string name) =>
        (parent as XElement)?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static IReadOnlyList<EniSlave> ParseSlaves(XDocument doc)
    {
        var slaves = new List<EniSlave>();
        foreach (var el in LocalDescendants(doc.Root, "Slave"))
        {
            var info = Local(el, "Info");
            if (info is null) continue;
            var physAddr = (ushort?)EniXmlValues.ParseNumber(Text(info, "PhysAddr"));
            if (physAddr is null) continue;
            slaves.Add(new EniSlave(
                Text(info, "Name") ?? $"Slave {physAddr}",
                physAddr.Value,
                (ushort)(EniXmlValues.ParseNumber(Text(info, "AutoIncAddr")) ?? 0),
                (uint)(EniXmlValues.ParseNumber(Text(info, "VendorId")) ?? 0),
                (uint)(EniXmlValues.ParseNumber(Text(info, "ProductCode")) ?? 0),
                (uint)(EniXmlValues.ParseNumber(Text(info, "RevisionNo")) ?? 0),
                ParseMailboxRange(Local(el, "Mailbox", "Send")),
                ParseMailboxRange(Local(el, "Mailbox", "Recv")),
                ParsePreviousPort(Local(el, "PreviousPort"))));
        }
        return slaves;
    }

    private static MailboxRange? ParseMailboxRange(XElement? el)
    {
        if (el is null) return null;
        var start = (ushort?)EniXmlValues.ParseNumber(Text(el, "Start"));
        var length = (ushort?)EniXmlValues.ParseNumber(Text(el, "Length"));
        return start is null || length is null ? null : new MailboxRange(start.Value, length.Value);
    }

    /// <summary>&lt;PreviousPort&gt; declares the upstream device and its port. Both halves must
    /// parse: a declared parent with an unreadable port is not a usable edge, and inventing
    /// port 0 for it would place a branch on the upstream port.</summary>
    private static EniPreviousPort? ParsePreviousPort(XElement? el)
    {
        if (el is null) return null;
        var physAddr = (ushort?)EniXmlValues.ParseNumber(Text(el, "PhysAddr"));
        var port = EniPreviousPort.ParsePort(Text(el, "Port"));
        return physAddr is null || port is null ? null : new EniPreviousPort(physAddr.Value, port.Value);
    }

    private static IReadOnlyList<EniCyclicCommand> ParseCyclic(XDocument doc)
    {
        var commands = new List<EniCyclicCommand>();
        foreach (var cyclic in LocalDescendants(doc.Root, "Cyclic"))
            foreach (var cmd in LocalDescendants(cyclic, "Cmd"))
            {
                var cmdNumber = EniXmlValues.ParseNumber(Text(cmd, "Cmd"));
                if (cmdNumber is null or < 0 or > 14) continue;
                var addr = EniXmlValues.ParseNumber(Text(cmd, "Addr"));
                uint rawAddress;
                if (addr is not null)
                {
                    rawAddress = (uint)addr.Value;
                }
                else
                {
                    var adp = EniXmlValues.ParseNumber(Text(cmd, "Adp")) ?? 0;
                    var ado = EniXmlValues.ParseNumber(Text(cmd, "Ado")) ?? 0;
                    rawAddress = ((uint)ado << 16) | (ushort)adp;
                }
                commands.Add(new EniCyclicCommand(
                    (EtherCatCommand)cmdNumber.Value,
                    rawAddress,
                    (int)(EniXmlValues.ParseNumber(Text(cmd, "DataLength")) ?? 0),
                    (int)(EniXmlValues.ParseNumber(Text(cmd, "Cnt")) ?? 0),
                    (int?)EniXmlValues.ParseNumber(Text(cmd, "InputOffs")),
                    (int?)EniXmlValues.ParseNumber(Text(cmd, "OutputOffs"))));
            }
        return commands;
    }

    private static IReadOnlyList<EniVariable> ParseVariables(XDocument doc)
    {
        var variables = new List<EniVariable>();
        foreach (var image in LocalDescendants(doc.Root, "ProcessImage"))
        {
            foreach (var (section, isInput) in new[] { ("Inputs", true), ("Outputs", false) })
                foreach (var v in LocalDescendants(Local(image, section), "Variable"))
                {
                    var name = Text(v, "Name");
                    var bitOffs = (int?)EniXmlValues.ParseNumber(Text(v, "BitOffs"));
                    if (name is null || bitOffs is null) continue;
                    variables.Add(new EniVariable(
                        name,
                        Text(v, "DataType") ?? "UNKNOWN",
                        (int)(EniXmlValues.ParseNumber(Text(v, "BitSize")) ?? 0),
                        bitOffs.Value,
                        isInput));
                }
        }
        return variables;
    }
}
