using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace LiteSonic.Subsonic;

/// <summary>
/// Albero minimale per le risposte Subsonic, serializzato a mano in XML o JSON.
/// Niente reflection né serializer generici: compatibile con trimming/AOT e poche allocazioni.
/// </summary>
public sealed class Node(string name, bool isListItem = false)
{
    public string Name { get; } = name;
    /// <summary>In JSON gli elementi "di lista" diventano array anche se ce n'è uno solo.</summary>
    public bool IsListItem { get; set; } = isListItem;
    public List<KeyValuePair<string, object>> Attrs { get; } = [];
    public List<Node>? Children { get; private set; }
    /// <summary>Nomi di figli da emettere come array vuoto in JSON se non ce ne sono.</summary>
    public string? EmptyArray { get; private set; }
    /// <summary>Contenuto testuale (XML: testo dell'elemento, JSON: proprietà "value").</summary>
    public string? Text { get; set; }

    public static Node Item(string name) => new(name, true);

    public Node A(string key, object? value)
    {
        if (value is not null and not DBNull) Attrs.Add(new(key, value));
        return this;
    }

    public Node Add(Node child)
    {
        (Children ??= []).Add(child);
        return this;
    }

    public Node AddRange(IEnumerable<Node> children, string listName)
    {
        EmptyArray = listName;
        foreach (var c in children) Add(c);
        return this;
    }

    // ---------- XML ----------
    public static string ToXml(Node root)
    {
        var sb = new StringBuilder(1024);
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = false, Encoding = Encoding.UTF8 }))
        {
            WriteXml(w, root, true);
        }
        // StringBuilder forza encoding="utf-16" nella dichiarazione: la correggiamo
        return sb.Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"").ToString();
    }

    static void WriteXml(XmlWriter w, Node n, bool root)
    {
        if (root) w.WriteStartElement(n.Name, "http://subsonic.org/restapi");
        else w.WriteStartElement(n.Name);
        foreach (var (k, v) in n.Attrs) w.WriteAttributeString(k, Format(v));
        if (n.Text is not null) w.WriteString(n.Text);
        if (n.Children is not null)
            foreach (var c in n.Children) WriteXml(w, c, false);
        w.WriteEndElement();
    }

    static string Format(object v) => v switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    // ---------- JSON ----------
    public static void WriteJson(Utf8JsonWriter w, Node root)
    {
        w.WriteStartObject();
        w.WritePropertyName(root.Name);
        WriteJsonBody(w, root);
        w.WriteEndObject();
    }

    static void WriteJsonBody(Utf8JsonWriter w, Node n)
    {
        w.WriteStartObject();
        foreach (var (k, v) in n.Attrs)
        {
            switch (v)
            {
                case bool b: w.WriteBoolean(k, b); break;
                case int i: w.WriteNumber(k, i); break;
                case long l: w.WriteNumber(k, l); break;
                case double d: w.WriteNumber(k, d); break;
                default: w.WriteString(k, Format(v)); break;
            }
        }
        if (n.Text is not null) w.WriteString("value", n.Text);

        if (n.Children is { Count: > 0 })
        {
            // raggruppa per nome mantenendo l'ordine di prima apparizione
            var done = new HashSet<string>();
            foreach (var first in n.Children)
            {
                if (!done.Add(first.Name)) continue;
                bool asArray = first.IsListItem || n.Children.Count(c => c.Name == first.Name) > 1;
                w.WritePropertyName(first.Name);
                if (asArray)
                {
                    w.WriteStartArray();
                    foreach (var c in n.Children)
                        if (c.Name == first.Name) WriteJsonBody(w, c);
                    w.WriteEndArray();
                }
                else WriteJsonBody(w, first);
            }
        }
        else if (n.EmptyArray is not null)
        {
            w.WritePropertyName(n.EmptyArray);
            w.WriteStartArray();
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
}
