using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Omnipotent.Services.OmniBlox.Rbx
{
    /// <summary>
    /// Writes Roblox's XML format (.rbxlx / .rbxmx). XML rather than binary because each instance lists
    /// its own properties: instances merged from different files (or built here) need not share a
    /// property set, and anything left out takes the engine default. The Open Cloud place-publishing
    /// API accepts this format directly (Content-Type: application/xml).
    ///
    /// UniqueId and SecurityCapabilities values are dropped: Roblox mints fresh ones on load, and copying
    /// another file's UniqueIds into a place would duplicate them.
    /// </summary>
    public static class RbxXmlWriter
    {
        public static byte[] WriteBytes(RbxDocument doc)
        {
            using var ms = new MemoryStream();
            Write(doc, ms);
            return ms.ToArray();
        }

        public static string WriteString(RbxDocument doc) => Encoding.UTF8.GetString(WriteBytes(doc));

        public static void Write(RbxDocument doc, Stream output)
        {
            var referents = new Dictionary<RbxInstance, string>(ReferenceEqualityComparer.Instance);
            foreach (var inst in doc.AllInstances())
                referents[inst] = "RBX" + Guid.NewGuid().ToString("N").ToUpperInvariant();

            var shared = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                IndentChars = "\t",
                NewLineChars = "\n",
                OmitXmlDeclaration = true,
                CheckCharacters = false,
            };
            using var w = XmlWriter.Create(output, settings);
            w.WriteStartElement("roblox");
            w.WriteAttributeString("xmlns", "xmime", null, "http://www.w3.org/2005/05/xmlmime");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xsi", "noNamespaceSchemaLocation", "http://www.w3.org/2001/XMLSchema-instance", "http://www.roblox.com/roblox.xsd");
            w.WriteAttributeString("version", "4");

            foreach (var (key, value) in doc.Metadata)
            {
                w.WriteStartElement("Meta");
                w.WriteAttributeString("name", key);
                w.WriteString(Clean(value));
                w.WriteEndElement();
            }
            w.WriteElementString("External", "null");
            w.WriteElementString("External", "nil");

            foreach (var root in doc.Roots) WriteItem(w, root, referents, shared);

            if (shared.Count > 0)
            {
                w.WriteStartElement("SharedStrings");
                foreach (var (key, value) in shared)
                {
                    w.WriteStartElement("SharedString");
                    w.WriteAttributeString("md5", key);
                    w.WriteString(Convert.ToBase64String(value));
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.Flush();
        }

        private static void WriteItem(XmlWriter w, RbxInstance inst, Dictionary<RbxInstance, string> referents, Dictionary<string, byte[]> shared)
        {
            w.WriteStartElement("Item");
            w.WriteAttributeString("class", inst.ClassName);
            w.WriteAttributeString("referent", referents[inst]);
            w.WriteStartElement("Properties");
            foreach (var (name, value) in inst.Properties) WriteProperty(w, name, value, referents, shared);
            w.WriteEndElement();
            foreach (var child in inst.Children) WriteItem(w, child, referents, shared);
            w.WriteEndElement();
        }

        private static void WriteProperty(XmlWriter w, string name, RbxValue value, Dictionary<RbxInstance, string> referents, Dictionary<string, byte[]> shared)
        {
            switch (value.Type)
            {
                case RbxType.UniqueId:
                case RbxType.SecurityCapabilities:
                    return;
            }

            void Open(string element)
            {
                w.WriteStartElement(element);
                w.WriteAttributeString("name", name);
            }

            switch (value.Type)
            {
                case RbxType.String:
                    Open("string");
                    w.WriteString(Clean(value.Value as string ?? ""));
                    break;
                case RbxType.ProtectedString:
                    Open("ProtectedString");
                    WriteCData(w, Clean(value.Value as string ?? ""));
                    break;
                case RbxType.Content:
                    Open("Content");
                    {
                        string url = value.Value as string ?? "";
                        if (string.IsNullOrEmpty(url)) w.WriteElementString("null", "");
                        else w.WriteElementString("url", Clean(url));
                    }
                    break;
                case RbxType.BinaryString:
                    Open("BinaryString");
                    w.WriteString(Convert.ToBase64String(value.Value as byte[] ?? Array.Empty<byte>()));
                    break;
                case RbxType.Bool:
                    Open("bool");
                    w.WriteString((bool)value.Value! ? "true" : "false");
                    break;
                case RbxType.Int32:
                case RbxType.BrickColor:
                    Open("int");
                    w.WriteString(((int)value.Value!).ToString(CultureInfo.InvariantCulture));
                    break;
                case RbxType.Float32:
                    Open("float");
                    w.WriteString(F((float)value.Value!));
                    break;
                case RbxType.Float64:
                    Open("double");
                    w.WriteString(D((double)value.Value!));
                    break;
                case RbxType.Int64:
                    Open("int64");
                    w.WriteString(((long)value.Value!).ToString(CultureInfo.InvariantCulture));
                    break;
                case RbxType.UDim:
                    {
                        var u = (RbxUDim)value.Value!;
                        Open("UDim");
                        w.WriteElementString("S", F(u.Scale));
                        w.WriteElementString("O", u.Offset.ToString(CultureInfo.InvariantCulture));
                        break;
                    }
                case RbxType.UDim2:
                    {
                        var u = (RbxUDim2)value.Value!;
                        Open("UDim2");
                        w.WriteElementString("XS", F(u.X.Scale));
                        w.WriteElementString("XO", u.X.Offset.ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString("YS", F(u.Y.Scale));
                        w.WriteElementString("YO", u.Y.Offset.ToString(CultureInfo.InvariantCulture));
                        break;
                    }
                case RbxType.Ray:
                    {
                        var r = (RbxRay)value.Value!;
                        Open("Ray");
                        w.WriteStartElement("origin"); Vec3(w, r.Origin); w.WriteEndElement();
                        w.WriteStartElement("direction"); Vec3(w, r.Direction); w.WriteEndElement();
                        break;
                    }
                case RbxType.Faces:
                    Open("Faces");
                    w.WriteElementString("faces", ((byte)value.Value!).ToString(CultureInfo.InvariantCulture));
                    break;
                case RbxType.Axes:
                    Open("Axes");
                    w.WriteElementString("axes", ((byte)value.Value!).ToString(CultureInfo.InvariantCulture));
                    break;
                case RbxType.Color3:
                    {
                        var c = (RbxColor3)value.Value!;
                        Open("Color3");
                        w.WriteElementString("R", F(c.R));
                        w.WriteElementString("G", F(c.G));
                        w.WriteElementString("B", F(c.B));
                        break;
                    }
                case RbxType.Color3uint8:
                    Open("Color3uint8");
                    w.WriteString(((RbxColor3uint8)value.Value!).Packed.ToString(CultureInfo.InvariantCulture));
                    break;
                case RbxType.Vector2:
                    {
                        var v = (RbxVector2)value.Value!;
                        Open("Vector2");
                        w.WriteElementString("X", F(v.X));
                        w.WriteElementString("Y", F(v.Y));
                        break;
                    }
                case RbxType.Vector3:
                    Open("Vector3");
                    Vec3(w, (RbxVector3)value.Value!);
                    break;
                case RbxType.Vector3int16:
                    {
                        var v = (RbxVector3int16)value.Value!;
                        Open("Vector3int16");
                        w.WriteElementString("X", v.X.ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString("Y", v.Y.ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString("Z", v.Z.ToString(CultureInfo.InvariantCulture));
                        break;
                    }
                case RbxType.CFrame:
                    Open("CoordinateFrame");
                    CFrame(w, (RbxCFrame)value.Value!);
                    break;
                case RbxType.OptionalCFrame:
                    Open("OptionalCoordinateFrame");
                    if (value.Value is RbxCFrame oc)
                    {
                        w.WriteStartElement("CFrame");
                        CFrame(w, oc);
                        w.WriteEndElement();
                    }
                    break;
                case RbxType.Enum:
                    Open("token");
                    w.WriteString(((uint)value.Value!).ToString(CultureInfo.InvariantCulture));
                    break;
                case RbxType.Ref:
                    Open("Ref");
                    w.WriteString(value.Value is RbxInstance target && referents.TryGetValue(target, out var refId) ? refId : "null");
                    break;
                case RbxType.NumberSequence:
                    {
                        Open("NumberSequence");
                        var sb = new StringBuilder();
                        foreach (var k in (List<RbxNumberKeypoint>)value.Value!)
                            sb.Append(F(k.Time)).Append(' ').Append(F(k.Value)).Append(' ').Append(F(k.Envelope)).Append(' ');
                        w.WriteString(sb.ToString());
                        break;
                    }
                case RbxType.ColorSequence:
                    {
                        Open("ColorSequence");
                        var sb = new StringBuilder();
                        foreach (var k in (List<RbxColorKeypoint>)value.Value!)
                            sb.Append(F(k.Time)).Append(' ').Append(F(k.Color.R)).Append(' ').Append(F(k.Color.G)).Append(' ')
                              .Append(F(k.Color.B)).Append(' ').Append(F(k.Envelope)).Append(' ');
                        w.WriteString(sb.ToString());
                        break;
                    }
                case RbxType.NumberRange:
                    {
                        var r = (RbxNumberRange)value.Value!;
                        Open("NumberRange");
                        w.WriteString($"{F(r.Min)} {F(r.Max)} ");
                        break;
                    }
                case RbxType.Rect:
                    {
                        var r = (RbxRect)value.Value!;
                        Open("Rect2D");
                        w.WriteStartElement("min");
                        w.WriteElementString("X", F(r.Min.X));
                        w.WriteElementString("Y", F(r.Min.Y));
                        w.WriteEndElement();
                        w.WriteStartElement("max");
                        w.WriteElementString("X", F(r.Max.X));
                        w.WriteElementString("Y", F(r.Max.Y));
                        w.WriteEndElement();
                        break;
                    }
                case RbxType.PhysicalProperties:
                    {
                        var p = (RbxPhysicalProperties)value.Value!;
                        Open("PhysicalProperties");
                        w.WriteElementString("CustomPhysics", p.Custom ? "true" : "false");
                        if (p.Custom)
                        {
                            w.WriteElementString("Density", F(p.Density));
                            w.WriteElementString("Friction", F(p.Friction));
                            w.WriteElementString("Elasticity", F(p.Elasticity));
                            w.WriteElementString("FrictionWeight", F(p.FrictionWeight));
                            w.WriteElementString("ElasticityWeight", F(p.ElasticityWeight));
                            if (p.AcousticAbsorption is float a) w.WriteElementString("AcousticAbsorption", F(a));
                        }
                        break;
                    }
                case RbxType.SharedString:
                    {
                        byte[] data = value.Value as byte[] ?? Array.Empty<byte>();
                        string key = Convert.ToBase64String(MD5.HashData(data));
                        shared[key] = data;
                        Open("SharedString");
                        w.WriteString(key);
                        break;
                    }
                case RbxType.Font:
                    {
                        var f = (RbxFont)value.Value!;
                        Open("Font");
                        w.WriteStartElement("Family"); w.WriteElementString("url", Clean(f.Family)); w.WriteEndElement();
                        w.WriteElementString("Weight", f.Weight.ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString("Style", f.Style);
                        w.WriteStartElement("CachedFaceId");
                        if (string.IsNullOrEmpty(f.CachedFaceId)) w.WriteElementString("null", "");
                        else w.WriteElementString("url", Clean(f.CachedFaceId));
                        w.WriteEndElement();
                        break;
                    }
                default:
                    return;
            }
            w.WriteEndElement();
        }

        private static void Vec3(XmlWriter w, RbxVector3 v)
        {
            w.WriteElementString("X", F(v.X));
            w.WriteElementString("Y", F(v.Y));
            w.WriteElementString("Z", F(v.Z));
        }

        private static readonly string[] RotationNames = { "R00", "R01", "R02", "R10", "R11", "R12", "R20", "R21", "R22" };

        private static void CFrame(XmlWriter w, RbxCFrame c)
        {
            Vec3(w, c.Position);
            var m = c.Rotation;
            for (int i = 0; i < 9; i++) w.WriteElementString(RotationNames[i], F(m[i]));
        }

        private static void WriteCData(XmlWriter w, string text)
        {
            // "]]>" cannot appear inside a CDATA section: split it across two sections.
            int start = 0;
            while (true)
            {
                int idx = text.IndexOf("]]>", start, StringComparison.Ordinal);
                if (idx < 0)
                {
                    w.WriteCData(text[start..]);
                    return;
                }
                w.WriteCData(text.Substring(start, idx - start + 2));
                start = idx + 2;
            }
        }

        internal static string F(float f)
        {
            if (float.IsPositiveInfinity(f)) return "INF";
            if (float.IsNegativeInfinity(f)) return "-INF";
            if (float.IsNaN(f)) return "NAN";
            return f.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static string D(double d)
        {
            if (double.IsPositiveInfinity(d)) return "INF";
            if (double.IsNegativeInfinity(d)) return "-INF";
            if (double.IsNaN(d)) return "NAN";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>XML 1.0 forbids most control characters; Roblox text never needs them.</summary>
        internal static string Clean(string s)
        {
            bool ok = true;
            foreach (char ch in s)
                if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r' || ch == '￾' || ch == '￿') { ok = false; break; }
            if (ok) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                sb.Append(ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r' || ch == '￾' || ch == '￿' ? '�' : ch);
            return sb.ToString();
        }
    }
}
