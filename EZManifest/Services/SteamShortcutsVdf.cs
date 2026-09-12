using System.Text;

namespace EZManifest.Services;

internal static class SteamShortcutsVdf
{
    private const byte TypeObject = 0x00;
    private const byte TypeString = 0x01;
    private const byte TypeInt32 = 0x02;
    private const byte TypeUInt64 = 0x07;
    private const byte TypeEnd = 0x08;

    public sealed class Node
    {
        public byte Type { get; init; }
        public string Name { get; init; } = string.Empty;
        public string StringValue { get; set; } = string.Empty;
        public int IntValue { get; set; }
        public ulong UInt64Value { get; set; }
        public List<Node> Children { get; } = [];

        public Node? Child(string name) =>
            Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public string GetString(string name) => Child(name)?.StringValue ?? string.Empty;

        public int GetInt(string name) => Child(name)?.IntValue ?? 0;

        public void SetString(string name, string value)
        {
            Node? existing = Child(name);
            if (existing is not null)
            {
                existing.StringValue = value;
                return;
            }

            Children.Add(new Node { Type = TypeString, Name = name, StringValue = value });
        }

        public void SetInt(string name, int value)
        {
            Node? existing = Child(name);
            if (existing is not null)
            {
                existing.IntValue = value;
                return;
            }

            Children.Add(new Node { Type = TypeInt32, Name = name, IntValue = value });
        }
    }

    public static Node LoadOrCreate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return NewRoot();

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            byte type = reader.ReadByte();
            string name = ReadCString(reader);
            if (type != TypeObject || !name.Equals("shortcuts", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("shortcuts.vdf does not start with a shortcuts object.");

            Node root = new() { Type = TypeObject, Name = "shortcuts" };
            ReadChildren(reader, root);
            return root;
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Could not read Steam shortcuts:\n{path}", ex);
        }
    }

    public static void Save(string path, Node root)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[SteamShortcut] Backup failed: {ex.Message}");
            }
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteNode(writer, root);
        writer.Write(TypeEnd);
    }

    public static Node NewShortcut(int index, int appId, string appName, string exe, string startDir, string icon, string launchOptions)
    {
        var node = new Node { Type = TypeObject, Name = index.ToString() };
        node.SetInt("appid", appId);
        node.SetString("AppName", appName);
        node.SetString("Exe", exe);
        node.SetString("StartDir", startDir);
        node.SetString("icon", icon);
        node.SetString("ShortcutPath", string.Empty);
        node.SetString("LaunchOptions", launchOptions);
        node.SetInt("IsHidden", 0);
        node.SetInt("AllowDesktopConfig", 1);
        node.SetInt("AllowOverlay", 1);
        node.SetInt("OpenVR", 0);
        node.SetInt("Devkit", 0);
        node.SetString("DevkitGameID", string.Empty);
        node.SetInt("DevkitOverrideAppID", 0);
        node.SetInt("LastPlayTime", 0);
        node.SetString("FlatpakAppID", string.Empty);
        node.Children.Add(new Node { Type = TypeObject, Name = "tags" });
        return node;
    }

    private static Node NewRoot() => new() { Type = TypeObject, Name = "shortcuts" };

    private static void ReadChildren(BinaryReader reader, Node parent)
    {
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte type = reader.ReadByte();
            if (type == TypeEnd)
                return;

            string name = ReadCString(reader);
            var child = new Node { Type = type, Name = name };
            switch (type)
            {
                case TypeObject:
                    ReadChildren(reader, child);
                    break;
                case TypeString:
                    child.StringValue = ReadCString(reader);
                    break;
                case TypeInt32:
                    child.IntValue = reader.ReadInt32();
                    break;
                case TypeUInt64:
                    child.UInt64Value = reader.ReadUInt64();
                    break;
                default:
                    throw new InvalidDataException($"Unsupported VDF type 0x{type:X2} in shortcuts.vdf");
            }

            parent.Children.Add(child);
        }
    }

    private static void WriteNode(BinaryWriter writer, Node node)
    {
        writer.Write(node.Type);
        WriteCString(writer, node.Name);
        switch (node.Type)
        {
            case TypeObject:
                foreach (Node child in node.Children)
                    WriteNode(writer, child);
                writer.Write(TypeEnd);
                break;
            case TypeString:
                WriteCString(writer, node.StringValue);
                break;
            case TypeInt32:
                writer.Write(node.IntValue);
                break;
            case TypeUInt64:
                writer.Write(node.UInt64Value);
                break;
        }
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            byte b = reader.ReadByte();
            if (b == 0)
                break;
            bytes.Add(b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value ?? string.Empty));
        writer.Write((byte)0);
    }
}
