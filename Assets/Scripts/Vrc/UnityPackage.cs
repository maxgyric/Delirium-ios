// A .unitypackage (a tar.gz: one folder per asset GUID with "asset", "asset.meta", "pathname") read on the phone.
// The assets are written to a working folder by GUID; their paths and .meta texts kept in memory.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Vrc
{
    public sealed class PackageAsset
    {
        public string Guid, Path = "", Meta = "", File; // File: the asset's bytes on disk (null: a folder)
    }

    public static class UnityPackage
    {
        // Every asset of `package` into `workDir` (emptied first). `progress` 0..1 by bytes read.
        public static Dictionary<string, PackageAsset> Extract(string package, string workDir, Action<float> progress = null)
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            var assets = new Dictionary<string, PackageAsset>();
            using (var file = File.OpenRead(package))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            {
            var header = new byte[512];
            string longName = null;
            var copyBuf = new byte[1 << 16];
            while (ReadFull(gz, header, 512))
            {
                if (header[0] == 0) break; // the end blocks
                string name = longName ?? Str(header, 0, 100);
                longName = null;
                string prefix = Str(header, 345, 155);
                if (prefix.Length > 0 && header[257] == 'u') name = prefix + "/" + name; // ustar
                string sizeText = Str(header, 124, 12).Trim();
                long size = sizeText.Length > 0 ? Convert.ToInt64(sizeText, 8) : 0;
                char type = (char)header[156];
                long padded = (size + 511) / 512 * 512;
                if (type == 'L') // GNU long name
                {
                    var b = new byte[padded];
                    ReadFull(gz, b, (int)padded);
                    longName = Encoding.UTF8.GetString(b, 0, (int)size).TrimEnd('\0');
                    continue;
                }
                if (type == 'x' || type == 'g') { Skip(gz, padded, copyBuf); continue; } // pax headers
                name = name.Replace('\\', '/');
                while (name.StartsWith("./")) name = name.Substring(2);
                int slash = name.IndexOf('/');
                if (type == '5' || slash < 0 || size == 0 && !name.EndsWith("/asset"))
                {
                    Skip(gz, padded, copyBuf);
                    continue;
                }
                string guid = name.Substring(0, slash), part = name.Substring(slash + 1);
                if (!assets.TryGetValue(guid, out var a)) assets[guid] = a = new PackageAsset { Guid = guid };
                if (part == "asset")
                {
                    a.File = Path.Combine(workDir, guid);
                    using (var o = File.Create(a.File)) Copy(gz, o, size, copyBuf);
                    Skip(gz, padded - size, copyBuf);
                }
                else if (part == "pathname" || part == "asset.meta")
                {
                    var b = new byte[padded];
                    ReadFull(gz, b, (int)padded);
                    string text = Encoding.UTF8.GetString(b, 0, (int)size);
                    if (part == "pathname") a.Path = text.Split('\n')[0].Trim();
                    else a.Meta = text;
                }
                else Skip(gz, padded, copyBuf);
                if (progress != null) progress(file.Length > 0 ? (float)file.Position / file.Length : 0f);
            }
            }
            return assets;
        }

        static string Str(byte[] b, int at, int len)
        {
            int end = at;
            while (end < at + len && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, at, end - at);
        }

        static bool ReadFull(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        static void Copy(Stream from, Stream to, long n, byte[] buf)
        {
            while (n > 0)
            {
                int r = from.Read(buf, 0, (int)Math.Min(buf.Length, n));
                if (r <= 0) throw new EndOfStreamException("the package ends early");
                to.Write(buf, 0, r);
                n -= r;
            }
        }

        static void Skip(Stream s, long n, byte[] buf)
        {
            while (n > 0)
            {
                int r = s.Read(buf, 0, (int)Math.Min(buf.Length, n));
                if (r <= 0) return;
                n -= r;
            }
        }
    }
}
