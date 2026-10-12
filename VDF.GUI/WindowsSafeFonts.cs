// /*
//     Copyright (C) 2026 0x90d
//     Part of VideoDuplicateFinder, GPLv3-or-later.
// */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace VDF.GUI {
    /// <summary>
    /// The Win10 startup crash is in native SkiaSharp's DirectWrite font
    /// file-stream implementation (SkDWriteFontFileStream::read). Switching the
    /// renderer to CPU does not change where SKTypeface gets font bytes.
    ///
    /// Read already-installed Windows fonts into a dedicated Avalonia font
    /// collection instead. Skia creates typefaces from managed streams, not
    /// DirectWrite handles. Font files are never copied into the package or
    /// modified, and the normal system-font mode stays unchanged.
    /// </summary>
    internal static class WindowsSafeFonts {
        internal const string CollectionUri = "fonts:VdfSafe";
        internal const string DefaultFont = CollectionUri + "#Microsoft YaHei";

        private sealed class SafeFontCollection : FontCollectionBase {
            public override Uri Key { get; } = new(CollectionUri, UriKind.Absolute);
        }

        internal static FontManagerOptions CreateOptions() {
            var chinese = new FontFamily(DefaultFont);
            var emoji = new FontFamily(CollectionUri + "#Segoe UI Emoji");
            var symbol = new FontFamily(CollectionUri + "#Segoe UI Symbol");
            var mono = new FontFamily(CollectionUri + "#Consolas");
            var ui = new FontFamily(CollectionUri + "#Segoe UI");
            var maps = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

            static void Map(Dictionary<string, FontFamily> target, FontFamily value, params string[] names) {
                foreach (string name in names)
                    target[name] = value;
            }

            // Explicit XAML font names are mapped too; otherwise they bypass
            // DefaultFamilyName and access native DirectWrite typefaces.
            Map(maps, chinese, "Microsoft YaHei UI", "Microsoft YaHei",
                "Microsoft YaHei Light", "Microsoft YaHei UI Light",
                "DengXian", "SimSun", "SimHei", "PingFang SC",
                "Noto Sans CJK SC", "Noto Sans", "Arial Unicode MS");
            Map(maps, mono, "Consolas", "Cascadia Mono", "Menlo", "monospace");
            Map(maps, ui, "Segoe UI", "Arial", "Helvetica", "sans-serif");
            Map(maps, symbol, "Segoe UI Symbol");
            Map(maps, emoji, "Segoe UI Emoji");

            return new FontManagerOptions {
                DefaultFamilyName = DefaultFont,
                FontFallbacks = [
                    new FontFallback { FontFamily = chinese },
                    new FontFallback { FontFamily = symbol },
                    new FontFallback { FontFamily = emoji }
                ],
                FontFamilyMappings = maps,
            };
        }

        internal static string Register() {
            if (!OperatingSystem.IsWindows())
                return "non-Windows";

            var fonts = new SafeFontCollection();
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
                foreach (string name in new[] {
                    "msyh.ttc", "msyhbd.ttc", "segoeui.ttf",
                    "consola.ttf", "seguisym.ttf", "seguiemj.ttf"
                }) {
                    string filename = Path.Combine(path, name);
                    if (!File.Exists(filename))
                        continue;
                    // Use private, memory-backed bytes; never hand a native
                    // typeface a DirectWrite font-file handle. SKTypeface
                    // FromStream TAKES OWNERSHIP of the managed Stream, so do
                    // not dispose it here (or later native reads may crash).
                    var stream = new MemoryStream(File.ReadAllBytes(filename), writable: false);
                    if (fonts.TryAddGlyphTypeface(stream, out GlyphTypeface? face))
                        loaded.Add(face.FamilyName);
                }

                if (!loaded.Contains("Microsoft YaHei"))
                    throw new InvalidOperationException(
                        "Required Microsoft YaHei font is missing from the Windows font files.");

                FontManager.Current.AddFontCollection(fonts);
                return string.Join(", ", loaded.OrderBy(name => name));
            }
            catch {
                ((IDisposable)fonts).Dispose();
                throw;
            }
        }
    }
}
