using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Omnipotent.Services.OmniBlox.Rbx
{
    /// <summary>Reads Roblox's XML format (.rbxmx / .rbxlx) into the same tree the binary reader produces.</summary>
    public static class RbxXmlReader
    {
        public static bool LooksXml(ReadOnlySpan<byte> data)
        {
            int i = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3; // UTF-8 BOM
            while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\r' || data[i] == '\n')) i++;
            return data.Length - i >= 7 && data.Slice(i, 7).SequenceEqual("<roblox"u8);
        }

        public static RbxDocument Read(byte[] data)
        {
            XDocument xml;
            using (var ms = new MemoryStream(data))
            using (var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CheckCharacters = false }))
                xml = XDocument.Load(reader);

            var root = xml.Root ?? throw new FormatException("Empty XML document.");
            if (root.Name.LocalName != "roblox") throw new FormatException("Not a Roblox XML file.");

            var doc = new RbxDocument();
            var shared = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var sharedSection = root.Element("SharedStrings");
            if (sharedSection != null)
                foreach (var s in sharedSection.Elements("SharedString"))
                {
                    string key = (string?)s.Attribute("md5") ?? "";
                    try { shared[key] = Convert.FromBase64String(s.Value.Trim()); } catch (FormatException) { }
                }

            foreach (var meta in root.Elements("Meta"))
                doc.Metadata[(string?)meta.Attribute("name") ?? ""] = meta.Value;

            var byReferent = new Dictionary<string, RbxInstance>(StringComparer.Ordinal);
            var pendingRefs = new List<(RbxInstance Owner, string Property, string Referent)>();

            foreach (var item in root.Elements("Item"))
                doc.AddRoot(ReadItem(item, byReferent, pendingRefs, shared, doc));

            foreach (var (owner, property, referent) in pendingRefs)
                owner.Properties[property] = RbxValue.Reference(byReferent.TryGetValue(referent, out var target) ? target : null);

            return doc;
        }

        private static RbxInstance ReadItem(XElement item, Dictionary<string, RbxInstance> byReferent,
            List<(RbxInstance, string, string)> pendingRefs, Dictionary<string, byte[]> shared, RbxDocument doc)
        {
            var inst = new RbxInstance((string?)item.Attribute("class") ?? "Folder");
            string? referent = (string?)item.Attribute("referent");
            if (!string.IsNullOrEmpty(referent))
            {
                inst.Referent = referent;
                byReferent[referent] = inst;
            }

            var props = item.Element("Properties");
            if (props != null)
                foreach (var p in props.Elements())
                {
                    string name = (string?)p.Attribute("name") ?? "";
                    if (name.Length == 0) continue;
                    try
                    {
                        if (p.Name.LocalName == "Ref")
                        {
                            string target = p.Value.Trim();
                            if (target is "null" or "nil" or "") inst.Properties[name] = RbxValue.Reference(null);
                            else pendingRefs.Add((inst, name, target));
                            continue;
                        }
                        var value = ReadValue(p, shared);
                        if (value != null) inst.Properties[name] = value;
                        else doc.UndecodedProperties.Add($"{inst.ClassName}.{name}:{p.Name.LocalName}");
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                    {
                        doc.UndecodedProperties.Add($"{inst.ClassName}.{name}:{p.Name.LocalName} (malformed)");
                    }
                }

            foreach (var child in item.Elements("Item"))
                inst.AddChild(ReadItem(child, byReferent, pendingRefs, shared, doc));
            return inst;
        }

        private static RbxValue? ReadValue(XElement p, Dictionary<string, byte[]> shared)
        {
            switch (p.Name.LocalName)
            {
                case "string": return RbxValue.Str(p.Value);
                case "ProtectedString": return RbxValue.Source(p.Value);
                case "Content":
                    {
                        var url = p.Element("url");
                        return RbxValue.ContentUrl(url?.Value ?? (p.Elements().Any() ? "" : p.Value));
                    }
                case "BinaryString":
                    return new RbxValue(RbxType.BinaryString, string.IsNullOrWhiteSpace(p.Value) ? Array.Empty<byte>() : Convert.FromBase64String(p.Value.Trim()));
                case "bool": return RbxValue.Bool(p.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
                case "int": return RbxValue.Int(int.Parse(p.Value.Trim(), CultureInfo.InvariantCulture));
                case "BrickColor": return new RbxValue(RbxType.BrickColor, int.Parse(p.Value.Trim(), CultureInfo.InvariantCulture));
                case "float": return RbxValue.Float(F(p.Value));
                case "double": return RbxValue.Double(D(p.Value));
                case "int64": return new RbxValue(RbxType.Int64, long.Parse(p.Value.Trim(), CultureInfo.InvariantCulture));
                case "token": return RbxValue.Token(uint.Parse(p.Value.Trim(), CultureInfo.InvariantCulture));
                case "UDim": return new RbxValue(RbxType.UDim, new RbxUDim(F(C(p, "S")), I(C(p, "O"))));
                case "UDim2":
                    return RbxValue.UDim2(new RbxUDim2(new RbxUDim(F(C(p, "XS")), I(C(p, "XO"))), new RbxUDim(F(C(p, "YS")), I(C(p, "YO")))));
                case "Ray":
                    return new RbxValue(RbxType.Ray, new RbxRay(V3(p.Element("origin")!), V3(p.Element("direction")!)));
                case "Faces": return new RbxValue(RbxType.Faces, byte.Parse(C(p, "faces"), CultureInfo.InvariantCulture));
                case "Axes": return new RbxValue(RbxType.Axes, byte.Parse(C(p, "axes"), CultureInfo.InvariantCulture));
                case "Color3":
                    if (!p.HasElements) return RbxValue.Color(RbxColor3uint8.FromPacked(uint.Parse(p.Value.Trim(), CultureInfo.InvariantCulture)).ToColor3());
                    return RbxValue.Color(new RbxColor3(F(C(p, "R")), F(C(p, "G")), F(C(p, "B"))));
                case "Color3uint8": return RbxValue.Color8(RbxColor3uint8.FromPacked(uint.Parse(p.Value.Trim(), CultureInfo.InvariantCulture)));
                case "Vector2": return RbxValue.Vec2(new RbxVector2(F(C(p, "X")), F(C(p, "Y"))));
                case "Vector3": return RbxValue.Vec3(V3(p));
                case "Vector3int16":
                    return new RbxValue(RbxType.Vector3int16, new RbxVector3int16(short.Parse(C(p, "X"), CultureInfo.InvariantCulture),
                        short.Parse(C(p, "Y"), CultureInfo.InvariantCulture), short.Parse(C(p, "Z"), CultureInfo.InvariantCulture)));
                case "CoordinateFrame": return RbxValue.Frame(CF(p));
                case "OptionalCoordinateFrame":
                    {
                        var inner = p.Element("CFrame");
                        return new RbxValue(RbxType.OptionalCFrame, inner == null ? (RbxCFrame?)null : CF(inner));
                    }
                case "NumberSequence":
                    {
                        var n = Numbers(p.Value);
                        var keys = new List<RbxNumberKeypoint>();
                        for (int i = 0; i + 2 < n.Count; i += 3) keys.Add(new RbxNumberKeypoint(n[i], n[i + 1], n[i + 2]));
                        return new RbxValue(RbxType.NumberSequence, keys);
                    }
                case "ColorSequence":
                    {
                        var n = Numbers(p.Value);
                        var keys = new List<RbxColorKeypoint>();
                        for (int i = 0; i + 4 < n.Count; i += 5) keys.Add(new RbxColorKeypoint(n[i], new RbxColor3(n[i + 1], n[i + 2], n[i + 3]), n[i + 4]));
                        return new RbxValue(RbxType.ColorSequence, keys);
                    }
                case "NumberRange":
                    {
                        var n = Numbers(p.Value);
                        return new RbxValue(RbxType.NumberRange, new RbxNumberRange(n.Count > 0 ? n[0] : 0, n.Count > 1 ? n[1] : 0));
                    }
                case "Rect2D":
                    {
                        var min = p.Element("min")!;
                        var max = p.Element("max")!;
                        return new RbxValue(RbxType.Rect, new RbxRect(new RbxVector2(F(C(min, "X")), F(C(min, "Y"))), new RbxVector2(F(C(max, "X")), F(C(max, "Y")))));
                    }
                case "PhysicalProperties":
                    {
                        bool custom = C(p, "CustomPhysics").Equals("true", StringComparison.OrdinalIgnoreCase);
                        if (!custom) return new RbxValue(RbxType.PhysicalProperties, new RbxPhysicalProperties(false));
                        float? acoustic = p.Element("AcousticAbsorption") is { } a ? F(a.Value) : null;
                        return new RbxValue(RbxType.PhysicalProperties, new RbxPhysicalProperties(true, F(C(p, "Density")), F(C(p, "Friction")),
                            F(C(p, "Elasticity")), F(C(p, "FrictionWeight")), F(C(p, "ElasticityWeight")), acoustic));
                    }
                case "SharedString":
                    return new RbxValue(RbxType.SharedString, shared.TryGetValue(p.Value.Trim(), out var data) ? data : Array.Empty<byte>());
                case "Font":
                    {
                        string family = p.Element("Family")?.Element("url")?.Value ?? "";
                        ushort weight = ushort.TryParse(p.Element("Weight")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wgt) ? wgt : (ushort)400;
                        string style = p.Element("Style")?.Value ?? "Normal";
                        string cached = p.Element("CachedFaceId")?.Element("url")?.Value ?? "";
                        return new RbxValue(RbxType.Font, new RbxFont(family, weight, style, cached));
                    }
                case "UniqueId":
                case "SecurityCapabilities":
                    return null; // dropped on purpose; Roblox regenerates them
                default:
                    return null;
            }
        }

        private static string C(XElement parent, string child) => parent.Element(child)?.Value.Trim() ?? "0";

        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private static float F(string s)
        {
            s = s.Trim();
            return s.ToUpperInvariant() switch
            {
                "INF" or "+INF" => float.PositiveInfinity,
                "-INF" => float.NegativeInfinity,
                "NAN" or "-NAN" => float.NaN,
                _ => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
            };
        }

        private static double D(string s)
        {
            s = s.Trim();
            return s.ToUpperInvariant() switch
            {
                "INF" or "+INF" => double.PositiveInfinity,
                "-INF" => double.NegativeInfinity,
                "NAN" or "-NAN" => double.NaN,
                _ => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
            };
        }

        private static RbxVector3 V3(XElement e) => new(F(C(e, "X")), F(C(e, "Y")), F(C(e, "Z")));

        private static RbxCFrame CF(XElement e) => new(V3(e),
            F(C(e, "R00")), F(C(e, "R01")), F(C(e, "R02")),
            F(C(e, "R10")), F(C(e, "R11")), F(C(e, "R12")),
            F(C(e, "R20")), F(C(e, "R21")), F(C(e, "R22")));

        private static List<float> Numbers(string text) =>
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(F).ToList();
    }

    /// <summary>Reads either format by sniffing the first bytes.</summary>
    public static class RbxFile
    {
        public static RbxDocument Read(byte[] data)
        {
            if (RbxBinaryReader.LooksBinary(data)) return RbxBinaryReader.Read(data);
            if (RbxXmlReader.LooksXml(data)) return RbxXmlReader.Read(data);
            throw new FormatException("Not a Roblox model or place file.");
        }
    }
}
