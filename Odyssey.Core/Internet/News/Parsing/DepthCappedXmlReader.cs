using System.Xml;

namespace Odyssey.Core.Internet.News.Parsing;

/// <summary>
/// The wrapping reader every XML consumer reads through (issue #470 §7.10), the raw capture
/// (<see cref="System.Xml.Linq.XNode.ReadFrom"/>) included: it throws an <see cref="XmlException"/> the moment an
/// element nests deeper than <see cref="MaxDepth"/>, so a 100,000-deep document fails here, linearly, before anything
/// recursive sees it.
/// </summary>
internal sealed class DepthCappedXmlReader(XmlReader inner) : XmlReader, IXmlNamespaceResolver, IXmlLineInfo
{
    /// <summary>The depth cap (§7.10): the root element is depth 0.</summary>
    public const int MaxDepth = 64;

    public override int AttributeCount => inner.AttributeCount;

    public override string BaseURI => inner.BaseURI;

    public override int Depth => inner.Depth;

    public override bool EOF => inner.EOF;

    public override bool IsEmptyElement => inner.IsEmptyElement;

    public override string LocalName => inner.LocalName;

    public override string Name => inner.Name;

    public override string NamespaceURI => inner.NamespaceURI;

    public override XmlNameTable NameTable => inner.NameTable;

    public override XmlNodeType NodeType => inner.NodeType;

    public override string Prefix => inner.Prefix;

    public override ReadState ReadState => inner.ReadState;

    public override string Value => inner.Value;

    public override bool HasValue => inner.HasValue;

    public override bool IsDefault => inner.IsDefault;

    public override string XmlLang => inner.XmlLang;

    public override XmlSpace XmlSpace => inner.XmlSpace;

    public override XmlReaderSettings? Settings => inner.Settings;

    public int LineNumber => (inner as IXmlLineInfo)?.LineNumber ?? 0;

    public int LinePosition => (inner as IXmlLineInfo)?.LinePosition ?? 0;

    public override bool Read()
    {
        var read = inner.Read();
        if (read && inner.Depth > MaxDepth)
        {
            throw new XmlException($"The document nests deeper than {MaxDepth} levels.");
        }

        return read;
    }

    public override string GetAttribute(int i) => inner.GetAttribute(i);

    public override string? GetAttribute(string name) => inner.GetAttribute(name);

    public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);

    public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);

    public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);

    public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);

    public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);

    public override bool MoveToElement() => inner.MoveToElement();

    public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();

    public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();

    public override bool ReadAttributeValue() => inner.ReadAttributeValue();

    public override void ResolveEntity() => inner.ResolveEntity();

    public override void Close() => inner.Close();

    public IDictionary<string, string> GetNamespacesInScope(XmlNamespaceScope scope) =>
        (inner as IXmlNamespaceResolver)?.GetNamespacesInScope(scope) ?? new Dictionary<string, string>();

    public string? LookupPrefix(string namespaceName) => (inner as IXmlNamespaceResolver)?.LookupPrefix(namespaceName);

    public bool HasLineInfo() => inner is IXmlLineInfo info && info.HasLineInfo();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
