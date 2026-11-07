using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShiftConvertApp;

public sealed class ConvertOption
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string SubLabel { get; init; } = "";
    public string Icon { get; init; } = "";
    public Color Accent { get; init; }
}

public sealed class ConvertResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? OutputPath { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
}

public static class ConvertEngine
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff", ".tif", ".ico", ".wdp", ".jxr"
    };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".m4v"
    };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".csv", ".log", ".xml", ".html", ".htm", ".cs", ".py", ".js", ".ts", ".yaml", ".yml", ".ini", ".sql", ".sh", ".bat", ".ps1"
    };

    public static bool IsImageFile(string path) => ImageExts.Contains(Path.GetExtension(path));
    public static bool IsVideoFile(string path) => VideoExts.Contains(Path.GetExtension(path));
    public static bool IsTextFile(string path) => TextExts.Contains(Path.GetExtension(path));
    public static bool IsZipFile(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public static List<ConvertOption> GetOptionsForFiles(IReadOnlyList<string> files, bool forWheel = true)
    {
        string first = files.FirstOrDefault() ?? "";
        string ext = Path.GetExtension(first).ToLowerInvariant();
        bool isDir = Directory.Exists(first);
        bool allImages = files.Count > 0 && files.All(IsImageFile);
        bool allVideos = files.Count > 0 && files.All(IsVideoFile);
        bool isImg = !isDir && (allImages || IsImageFile(first));
        bool isVid = !isDir && (allVideos || IsVideoFile(first));
        bool isTxt = !isDir && IsTextFile(first);
        bool isZip = !isDir && IsZipFile(first);

        if (isImg)
        {
            string currentKey = ext switch
            {
                ".jpg" or ".jpeg" => "jpg",
                ".tif" or ".tiff" => "tiff",
                _ => ext.TrimStart('.')
            };

            string pdfSub = files.Count > 1
                ? Loc.T($"Склеить {files.Count} шт.", $"Merge {files.Count}", $"{files.Count} birleştir")
                : Loc.T("Документ", "Document", "Belge");

            var lime = Color.FromRgb(0xC6, 0xF2, 0x4E);
            var allImgOptions = new List<ConvertOption>
            {
                new() { Key = "png", Label = "PNG", SubLabel = Loc.T("Без потерь", "Lossless", "Kayıpsız"), Icon = "\uEB9F", Accent = lime },
                new() { Key = "jpg", Label = "JPG", SubLabel = Loc.T("Фото 92%", "Photo 92%", "Foto 92%"), Icon = "\uE722", Accent = lime },
                new() { Key = "webp", Label = "WEBP", SubLabel = Loc.T("Для веба", "Web Format", "Web Formatı"), Icon = "\uE774", Accent = lime },
                new() { Key = "pdf", Label = "PDF", SubLabel = pdfSub, Icon = "\uEA90", Accent = lime },
                new() { Key = "ocr", Label = "OCR", SubLabel = Loc.T("Текст с фото", "Extract Text", "Metni Al"), Icon = "\uE8EC", Accent = lime },
                new() { Key = "compress", Label = Loc.T("Сжать", "Compress", "Sıkıştır"), SubLabel = Loc.T("Меньше МБ", "Smaller size", "Küçült"), Icon = "\uE740", Accent = lime },
                new() { Key = "ico", Label = "ICO", SubLabel = Loc.T("Иконка", "App Icon", "Simge"), Icon = "\uE737", Accent = lime },
                new() { Key = "square", Label = "1:1", SubLabel = Loc.T("Квадрат/Аватар", "Square Crop", "Kare Kırp"), Icon = "\uE7FB", Accent = lime },
                new() { Key = "base64", Label = "Base64", SubLabel = Loc.T("Код Data-URI", "Data-URI Code", "Data-URI"), Icon = "\uE943", Accent = lime },
                new() { Key = "grayscale", Label = Loc.T("Ч/Б", "B&W", "S/B"), SubLabel = Loc.T("Монохром", "Grayscale", "Gri Ton"), Icon = "\uE790", Accent = lime },
                new() { Key = "zip", Label = "ZIP", SubLabel = Loc.T("В архив", "Archive", "Arşivle"), Icon = "\uF012", Accent = lime },
                new() { Key = "bmp", Label = "BMP", SubLabel = Loc.T("Растр", "Bitmap", "Bitmap"), Icon = "\uEB9F", Accent = lime },
                new() { Key = "gif", Label = "GIF", SubLabel = Loc.T("Кадр", "GIF Frame", "GIF Kare"), Icon = "\uE8B2", Accent = lime },
                new() { Key = "clipboard", Label = Loc.T("В буфер", "Copy", "Panoya"), SubLabel = Loc.T("Скопировать", "To Clipboard", "Kopyala"), Icon = "\uE8C8", Accent = lime },
            };

            bool sameExt = files.All(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase));
            var filtered = sameExt
                ? allImgOptions.Where(o => !string.Equals(o.Key, currentKey, StringComparison.OrdinalIgnoreCase)).ToList()
                : allImgOptions;

            if (forWheel && filtered.Count > 8)
            {
                var top7 = filtered.Take(7).ToList();
                var clipOpt = filtered.FirstOrDefault(o => o.Key == "clipboard");
                if (clipOpt != null && !top7.Contains(clipOpt))
                {
                    top7.Add(clipOpt);
                }
                return top7;
            }
            return filtered;
        }

        var accentLime = Color.FromRgb(0xC6, 0xF2, 0x4E);

        if (isVid)
        {
            var vidList = new List<ConvertOption>
            {
                new() { Key = "mp3", Label = "MP3", SubLabel = Loc.T("Извлечь звук", "Extract Audio", "Sesi Çıkart"), Icon = "\uE8D6", Accent = accentLime },
                new() { Key = "vid_compress", Label = Loc.T("Сжать", "Compress", "Sıkıştır"), SubLabel = Loc.T("Меньше МБ (720p)", "Smaller 720p", "Küçült 720p"), Icon = "\uE740", Accent = accentLime },
            };

            if (files.Count >= 2)
            {
                vidList.Add(new()
                {
                    Key = "mp4",
                    Label = "MP4",
                    SubLabel = Loc.T($"Склеить {files.Count} видео", $"Merge {files.Count} videos", $"{files.Count} birleştir"),
                    Icon = "\uE714",
                    Accent = accentLime
                });
            }
            else if (!string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                vidList.Add(new()
                {
                    Key = "mp4",
                    Label = "MP4",
                    SubLabel = Loc.T("Универсальный H.264", "Universal H.264", "Evrensel MP4"),
                    Icon = "\uE714",
                    Accent = accentLime
                });
            }

            vidList.Add(new() { Key = "vid_gif", Label = "GIF", SubLabel = Loc.T("Анимация из видео", "Animated GIF", "Animasyon GIF"), Icon = "\uE8B2", Accent = accentLime });
            vidList.Add(new() { Key = "vid_mute", Label = Loc.T("Без звука", "Mute", "Sessiz"), SubLabel = Loc.T("Убрать микрофон", "Strip Audio", "Sesi Kaldır"), Icon = "\uE74F", Accent = accentLime });
            vidList.Add(new() { Key = "vid_frame", Label = Loc.T("Кадр PNG", "Frame PNG", "Kare PNG"), SubLabel = Loc.T("Обложка в буфер", "Cover to Clipboard", "Kapak Kopyala"), Icon = "\uEB9F", Accent = accentLime });
            vidList.Add(new() { Key = "zip", Label = "ZIP", SubLabel = Loc.T("В архив", "Archive", "Arşivle"), Icon = "\uF012", Accent = accentLime });
            vidList.Add(new() { Key = "sha256", Label = "SHA-256", SubLabel = Loc.T("Хеш файла", "Copy Hash", "Hash Kopyala"), Icon = "\uE72E", Accent = accentLime });
            vidList.Add(new() { Key = "clipboard", Label = Loc.T("В буфер", "Copy", "Panoya"), SubLabel = Loc.T("Скопировать", "To Clipboard", "Kopyala"), Icon = "\uE8C8", Accent = accentLime });

            return forWheel && vidList.Count > 8 ? vidList.Take(8).ToList() : vidList;
        }

        if (isTxt)
        {
            var txtList = new List<ConvertOption>();
            if (ext == ".csv")
            {
                txtList.Add(new() { Key = "csv_to_json", Label = "JSON", SubLabel = Loc.T("Из таблицы CSV", "From CSV", "CSV'den JSON"), Icon = "\uE9F9", Accent = accentLime });
                txtList.Add(new() { Key = "html", Label = "HTML", SubLabel = Loc.T("Веб-таблица", "HTML Table", "HTML Tablo"), Icon = "\uE774", Accent = accentLime });
            }
            else if (ext == ".json")
            {
                txtList.Add(new() { Key = "json_to_csv", Label = "CSV", SubLabel = Loc.T("Таблица Excel", "Excel CSV", "CSV Tablo"), Icon = "\uE9F9", Accent = accentLime });
                txtList.Add(new() { Key = "format_json", Label = "Pretty", SubLabel = Loc.T("Формат JSON", "Format JSON", "JSON Düzenle"), Icon = "\uE943", Accent = accentLime });
                txtList.Add(new() { Key = "html", Label = "HTML", SubLabel = Loc.T("Веб-просмотр", "Web View", "Web Görünüm"), Icon = "\uE774", Accent = accentLime });
            }
            else
            {
                txtList.Add(new() { Key = "html", Label = "HTML", SubLabel = ext == ".md" ? Loc.T("Вёрстка Markdown", "Markdown Web", "Markdown Web") : Loc.T("Веб-страница", "Web Page", "Web Sayfası"), Icon = "\uE774", Accent = accentLime });
            }

            txtList.Add(new() { Key = "pdf", Label = "PDF", SubLabel = Loc.T("Документ", "Document", "Belge"), Icon = "\uEA90", Accent = accentLime });
            if (ext != ".txt")
            {
                txtList.Add(new() { Key = "txt", Label = "TXT", SubLabel = Loc.T("Чистый текст", "Plain Text", "Düz Metin"), Icon = "\uE8A5", Accent = accentLime });
            }
            txtList.Add(new() { Key = "zip", Label = "ZIP", SubLabel = Loc.T("В архив", "Compress ZIP", "ZIP Arşiv"), Icon = "\uF012", Accent = accentLime });
            txtList.Add(new() { Key = "sha256", Label = "SHA-256", SubLabel = Loc.T("Хеш в буфер", "Copy Hash", "Hash Kopyala"), Icon = "\uE72E", Accent = accentLime });
            txtList.Add(new() { Key = "clipboard", Label = Loc.T("В буфер", "Copy", "Panoya"), SubLabel = Loc.T("Скопировать текст", "Copy Text", "Metni Kopyala"), Icon = "\uE8C8", Accent = accentLime });

            return forWheel && txtList.Count > 8 ? txtList.Take(8).ToList() : txtList;
        }

        if (isZip)
        {
            return new List<ConvertOption>
            {
                new() { Key = "unzip", Label = Loc.T("Распаковать", "Extract", "Çıkart"), SubLabel = Loc.T("В папку рядом", "To Folder", "Klasöre Çıkart"), Icon = "\uE8B7", Accent = accentLime },
                new() { Key = "sha256", Label = "SHA-256", SubLabel = Loc.T("Хеш архива", "Verify Hash", "Hash Kopyala"), Icon = "\uE72E", Accent = accentLime },
                new() { Key = "clipboard", Label = Loc.T("Путь", "Copy Path", "Yolu Kopyala"), SubLabel = Loc.T("В буфер обмена", "To Clipboard", "Panoya"), Icon = "\uE8C8", Accent = accentLime },
            };
        }

        var genericList = new List<ConvertOption>
        {
            new() { Key = "zip", Label = Loc.T("В ZIP", "To ZIP", "ZIP Yap"), SubLabel = Loc.T("Сжать в архив", "Create Archive", "Arşiv Oluştur"), Icon = "\uF012", Accent = accentLime },
        };
        if (!isDir)
        {
            genericList.Add(new() { Key = "sha256", Label = "SHA-256", SubLabel = Loc.T("Контрольная сумма", "Copy File Hash", "Dosya Hash"), Icon = "\uE72E", Accent = accentLime });
            genericList.Add(new() { Key = "base64", Label = "Base64", SubLabel = Loc.T("В буфер обмена", "Encode Base64", "Base64 Kopyala"), Icon = "\uE943", Accent = accentLime });
        }
        genericList.Add(new() { Key = "clipboard", Label = Loc.T("Путь", "Copy Path", "Yolu Kopyala"), SubLabel = Loc.T("В буфер обмена", "To Clipboard", "Panoya"), Icon = "\uE8C8", Accent = accentLime });
        return genericList;
    }

    private static ConvertResult CreateCancelledResult() => new()
    {
        Success = false,
        Cancelled = true,
        Title = Loc.T("Конвертация отменена", "Conversion Cancelled", "Dönüştürme İptal Edildi"),
        Message = Loc.T("Операция остановлена, неполные файлы удалены", "Operation stopped, partial files removed", "İşlem durduruldu, eksik dosyalar silindi")
    };

    public static ConvertResult ConvertFiles(IReadOnlyList<string> files, string targetKey, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            return new ConvertResult { Success = false, Title = "ShiftConvert", Message = Loc.T("Файлы не выбраны", "No files selected", "Dosya seçilmedi") };
        }

        AppConfig.Load();
        bool autoCopy = AppConfig.Data.AutoCopyClipboard;
        string suffixCtrlV = autoCopy ? " · Ctrl+V" : "";

        try
        {
            ct.ThrowIfCancellationRequested();

            if (targetKey == "zip")
            {
                onProgress?.Invoke(3);
                string zipPath = CreateZipFromItems(files, onProgress, ct);
                onProgress?.Invoke(99);
                if (autoCopy) CopyOutputToClipboard(new[] { zipPath }, null);
                long zipBytes = new FileInfo(zipPath).Length;
                long srcBytes = files.Where(File.Exists).Sum(p => new FileInfo(p).Length);
                string sizeStr = FormatBytes(zipBytes);
                AppConfig.AddHistory(zipPath, "ZIP", sizeStr, Math.Max(0, srcBytes - zipBytes), files.Count);
                return new ConvertResult
                {
                    Success = true,
                    OutputPath = zipPath,
                    Title = Loc.T("Упаковано в ZIP", "Packed to ZIP", "ZIP Oluşturuldu"),
                    Message = $"{Path.GetFileName(zipPath)} ({sizeStr}){suffixCtrlV}"
                };
            }

            if (targetKey == "unzip")
            {
                string? lastDir = null;
                var zipFiles = files.Where(IsZipFile).ToList();
                for (int i = 0; i < zipFiles.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string f = zipFiles[i];
                    string destDir = GetUniqueDirectoryPath(f);
                    ExtractZipToDirectoryWithCancel(f, destDir, pct =>
                    {
                        double overall = ((i + pct / 100.0) / Math.Max(1, zipFiles.Count)) * 100.0;
                        onProgress?.Invoke(overall);
                    }, ct);
                    lastDir = destDir;
                }
                if (lastDir != null)
                {
                    AppConfig.AddHistory(lastDir, "UNZIP", "", 0, files.Count);
                    return new ConvertResult
                    {
                        Success = true,
                        OutputPath = lastDir,
                        Title = Loc.T("Архив распакован", "Archive Extracted", "Arşiv Çıkartıldı"),
                        Message = Path.GetFileName(lastDir)
                    };
                }
            }

            var vidFiles = files.Where(f => File.Exists(f) && IsVideoFile(f)).ToList();
            if (targetKey == "mp4" && vidFiles.Count >= 2)
            {
                string? mergedMp4 = MergeVideosAsMp4Async(vidFiles, onProgress, ct).GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(mergedMp4) && File.Exists(mergedMp4))
                {
                    if (autoCopy) CopyOutputToClipboard(new[] { mergedMp4 }, null);
                    string sizeStr = FormatBytes(new FileInfo(mergedMp4).Length);
                    AppConfig.AddHistory(mergedMp4, "MP4", sizeStr, 0, vidFiles.Count);
                    return new ConvertResult
                    {
                        Success = true,
                        OutputPath = mergedMp4,
                        Title = Loc.T($"Склеено {vidFiles.Count} видео в MP4", $"Merged {vidFiles.Count} videos to MP4", $"{vidFiles.Count} Video MP4'e Birleştirildi"),
                        Message = $"{Path.GetFileName(mergedMp4)} ({sizeStr}){suffixCtrlV}"
                    };
                }
            }

            var imgFiles = files.Where(f => File.Exists(f) && IsImageFile(f)).ToList();
            if (targetKey == "pdf" && imgFiles.Count >= 2)
            {
                string mergedPdf = SaveImagesAsMultiPagePdf(imgFiles, ct);
                if (autoCopy) CopyOutputToClipboard(new[] { mergedPdf }, null);
                string sizeStr = FormatBytes(new FileInfo(mergedPdf).Length);
                AppConfig.AddHistory(mergedPdf, "PDF", sizeStr, 0, imgFiles.Count);
                return new ConvertResult
                {
                    Success = true,
                    OutputPath = mergedPdf,
                    Title = Loc.T($"Склеено {imgFiles.Count} стр. в PDF", $"Merged {imgFiles.Count} pages to PDF", $"{imgFiles.Count} Sayfa PDF'e Birleştirildi"),
                    Message = $"{Path.GetFileName(mergedPdf)} ({sizeStr}){suffixCtrlV}"
                };
            }

            if (targetKey == "ocr")
            {
                var sb = new StringBuilder();
                foreach (string f in imgFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    string recognized = RunWindowsOcrAsync(f).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(recognized))
                    {
                        if (sb.Length > 0) sb.AppendLine().AppendLine("---");
                        sb.Append(recognized.Trim());
                    }
                }
                string finalOcr = sb.ToString().Trim();
                if (string.IsNullOrEmpty(finalOcr))
                {
                    return new ConvertResult
                    {
                        Success = false,
                        Title = Loc.T("Текст не найден", "No Text Found", "Metin Bulunamadı"),
                        Message = Loc.T("На изображении не удалось распознать текст", "Could not recognize text on image", "Görselde metin algılanamadı")
                    };
                }
                CopyTextSafe(finalOcr);
                string preview = finalOcr.Replace("\r", " ").Replace("\n", " ");
                if (preview.Length > 58) preview = preview[..58] + "…";
                return new ConvertResult
                {
                    Success = true,
                    Title = Loc.T("Текст скопирован (OCR)", "OCR Text Copied", "Metin Kopyalandı (OCR)"),
                    Message = $"«{preview}»"
                };
            }

            if (targetKey == "sha256")
            {
                string firstFile = files.FirstOrDefault(File.Exists) ?? "";
                if (!string.IsNullOrEmpty(firstFile))
                {
                    using var fs = new FileStream(firstFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    string hex = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                    CopyTextSafe(hex);
                    return new ConvertResult
                    {
                        Success = true,
                        Title = Loc.T("SHA-256 скопирован", "SHA-256 Copied", "SHA-256 Kopyalandı"),
                        Message = $"{hex[..16]}… ({Path.GetFileName(firstFile)})"
                    };
                }
            }

            if (targetKey == "base64")
            {
                string firstFile = files.FirstOrDefault(File.Exists) ?? "";
                if (!string.IsNullOrEmpty(firstFile))
                {
                    byte[] rawBytes = File.ReadAllBytes(firstFile);
                    string b64 = Convert.ToBase64String(rawBytes);
                    if (IsImageFile(firstFile))
                    {
                        string ext = Path.GetExtension(firstFile).ToLowerInvariant().TrimStart('.');
                        if (ext == "jpg") ext = "jpeg";
                        if (ext == "ico") ext = "x-icon";
                        b64 = $"data:image/{ext};base64,{b64}";
                    }
                    CopyTextSafe(b64);
                    return new ConvertResult
                    {
                        Success = true,
                        Title = Loc.T("Base64 скопирован в буфер", "Base64 Copied", "Base64 Kopyalandı"),
                        Message = $"{Path.GetFileName(firstFile)} ({FormatBytes(b64.Length)})"
                    };
                }
            }
        }
        catch (OperationCanceledException)
        {
            return CreateCancelledResult();
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return CreateCancelledResult();
            return new ConvertResult
            {
                Success = false,
                Title = Loc.T("Ошибка обработки", "Processing Error", "İşlem Hatası"),
                Message = ex.Message
            };
        }

        string? lastOutput = null;
        var createdFiles = new List<string>();
        BitmapSource? lastConvertedBitmap = null;
        int okCount = 0;
        long totalOldBytes = 0;
        long totalNewBytes = 0;

        foreach (string file in files)
        {
            if (ct.IsCancellationRequested)
            {
                foreach (string partial in createdFiles)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                }
                return CreateCancelledResult();
            }

            if (!File.Exists(file) && !Directory.Exists(file)) continue;
            try
            {
                if (File.Exists(file)) totalOldBytes += new FileInfo(file).Length;

                if (targetKey == "clipboard")
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (File.Exists(file) && IsImageFile(file))
                        {
                            var bmp = LoadBitmap(file);
                            if (bmp != null)
                            {
                                var dataObj = new DataObject();
                                var dropList = new StringCollection { file };
                                dataObj.SetFileDropList(dropList);
                                dataObj.SetImage(bmp);
                                Clipboard.SetDataObject(dataObj, true);
                            }
                        }
                        else if (File.Exists(file) && IsTextFile(file))
                        {
                            Clipboard.SetText(File.ReadAllText(file));
                        }
                        else
                        {
                            Clipboard.SetText(file);
                        }
                    });
                    lastOutput = file;
                    okCount++;
                    continue;
                }

                string? outPath = ConvertSingleFile(file, targetKey, out BitmapSource? outBmp, onProgress, ct);
                if (ct.IsCancellationRequested)
                {
                    if (!string.IsNullOrEmpty(outPath))
                    {
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    }
                    foreach (string partial in createdFiles)
                    {
                        try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                    }
                    return CreateCancelledResult();
                }

                if (!string.IsNullOrEmpty(outPath) && File.Exists(outPath))
                {
                    lastOutput = outPath;
                    createdFiles.Add(outPath);
                    if (outBmp != null) lastConvertedBitmap = outBmp;
                    totalNewBytes += new FileInfo(outPath).Length;
                    okCount++;
                }
            }
            catch (OperationCanceledException)
            {
                foreach (string partial in createdFiles)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                }
                return CreateCancelledResult();
            }
            catch
            {
                if (ct.IsCancellationRequested)
                {
                    foreach (string partial in createdFiles)
                    {
                        try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                    }
                    return CreateCancelledResult();
                }
            }
        }

        if (okCount == 0)
        {
            return new ConvertResult
            {
                Success = false,
                Title = Loc.T("Ошибка конвертации", "Conversion Failed", "Dönüştürme Hatası"),
                Message = Loc.T("Не удалось обработать выбранный формат", "Could not convert selected file", "Seçilen dosya dönüştürülemedi")
            };
        }

        if (targetKey == "clipboard")
        {
            return new ConvertResult
            {
                Success = true,
                OutputPath = lastOutput,
                Title = Loc.T("Скопировано в буфер", "Copied to Clipboard", "Panoya Kopyalandı"),
                Message = Path.GetFileName(lastOutput ?? "")
            };
        }

        if (createdFiles.Count > 0 && AppConfig.Data.AutoCopyClipboard)
        {
            CopyOutputToClipboard(createdFiles, createdFiles.Count == 1 ? lastConvertedBitmap : null);
        }

        string sizeInfo = totalOldBytes > 0 && totalNewBytes > 0
            ? $"{FormatBytes(totalOldBytes)} → {FormatBytes(totalNewBytes)}"
            : "";

        string labelUpper = targetKey switch
        {
            "csv_to_json" => "JSON",
            "json_to_csv" => "CSV",
            "format_json" => "JSON",
            "square" => "1:1 PNG",
            "grayscale" => "B&W PNG",
            "compress" => "JPG",
            "vid_compress" => "MP4 720P",
            "vid_gif" => "GIF",
            "vid_mute" => "MUTE MP4",
            "vid_frame" => "FRAME PNG",
            _ => targetKey.ToUpperInvariant()
        };

        if (!string.IsNullOrEmpty(lastOutput))
        {
            long savedBytes = Math.Max(0, totalOldBytes - totalNewBytes);
            AppConfig.AddHistory(lastOutput, labelUpper, sizeInfo, savedBytes, okCount);
        }

        string fileName = Path.GetFileName(lastOutput ?? "");
        string sizeBracket = !string.IsNullOrEmpty(sizeInfo) ? $" ({sizeInfo})" : "";
        string body = okCount == 1
            ? $"{fileName}{sizeBracket}{suffixCtrlV}"
            : Loc.T($"Готово файлов: {okCount}{sizeBracket}{suffixCtrlV}", $"Converted {okCount} files{sizeBracket}{suffixCtrlV}", $"{okCount} dosya dönüştürüldü{sizeBracket}{suffixCtrlV}");

        return new ConvertResult
        {
            Success = true,
            OutputPath = lastOutput,
            Title = Loc.T($"Готово: {labelUpper}", $"Converted to {labelUpper}", $"{labelUpper} Hazır"),
            Message = body
        };
    }

    private static void CopyOutputToClipboard(IReadOnlyList<string> filePaths, BitmapSource? singleBitmap)
    {
        if (filePaths.Count == 0) return;
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var dataObj = new DataObject();
                var dropList = new StringCollection();
                foreach (string p in filePaths)
                {
                    if (File.Exists(p)) dropList.Add(p);
                }
                if (dropList.Count > 0) dataObj.SetFileDropList(dropList);
                if (singleBitmap != null) dataObj.SetImage(singleBitmap);
                Clipboard.SetDataObject(dataObj, true);
            });
        }
        catch { }
    }

    private static void CopyTextSafe(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Application.Current?.Dispatcher.Invoke(() => Clipboard.SetText(text));
        }
        catch { }
    }

    private static string? ConvertSingleFile(string inputPath, string targetKey, out BitmapSource? producedBitmap, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        producedBitmap = null;
        ct.ThrowIfCancellationRequested();
        if (IsVideoFile(inputPath))
        {
            switch (targetKey)
            {
                case "mp3":
                    return ExtractAudioFromVideoAsync(inputPath, onProgress, ct).GetAwaiter().GetResult();
                case "vid_compress":
                    return TranscodeVideoAsync(inputPath, compress720p: true, stripAudio: false, onProgress, ct).GetAwaiter().GetResult();
                case "mp4":
                    return TranscodeVideoAsync(inputPath, compress720p: false, stripAudio: false, onProgress, ct).GetAwaiter().GetResult();
                case "vid_mute":
                    return TranscodeVideoAsync(inputPath, compress720p: false, stripAudio: true, onProgress, ct).GetAwaiter().GetResult();
                case "vid_gif":
                    return ConvertVideoToAnimatedGifAsync(inputPath, onProgress, ct).GetAwaiter().GetResult();
                case "vid_frame":
                {
                    var (framePath, frameBmp) = ExtractVideoFramePngAsync(inputPath, onProgress, ct).GetAwaiter().GetResult();
                    producedBitmap = frameBmp;
                    return framePath;
                }
                default:
                    return null;
            }
        }

        if (IsImageFile(inputPath))
        {
            var bmp = LoadBitmap(inputPath);
            if (bmp == null) return null;

            switch (targetKey)
            {
                case "png":
                    producedBitmap = bmp;
                    return SaveEncodedImage(inputPath, ".png", bmp, new PngBitmapEncoder());
                case "jpg":
                {
                    var flat = FlattenTransparency(bmp, Colors.White);
                    producedBitmap = flat;
                    return SaveEncodedImage(inputPath, ".jpg", flat, new JpegBitmapEncoder { QualityLevel = 92 });
                }
                case "webp":
                    producedBitmap = bmp;
                    return SaveAsWebp(inputPath, bmp);
                case "bmp":
                {
                    var flat = FlattenTransparency(bmp, Colors.White);
                    producedBitmap = flat;
                    return SaveEncodedImage(inputPath, ".bmp", flat, new BmpBitmapEncoder());
                }
                case "gif":
                    producedBitmap = bmp;
                    return SaveEncodedImage(inputPath, ".gif", bmp, new GifBitmapEncoder());
                case "tiff":
                    producedBitmap = bmp;
                    return SaveEncodedImage(inputPath, ".tiff", bmp, new TiffBitmapEncoder());
                case "compress":
                    return CompressImage(inputPath, bmp, out producedBitmap);
                case "square":
                {
                    var sq = CropSquareImage(bmp);
                    producedBitmap = sq;
                    return SaveEncodedImage(inputPath, ".png", sq, new PngBitmapEncoder(), "_square");
                }
                case "grayscale":
                {
                    var gray = ConvertToGrayscale(bmp);
                    producedBitmap = gray;
                    return SaveEncodedImage(inputPath, ".png", gray, new PngBitmapEncoder(), "_bw");
                }
                case "ico":
                    return SaveAsIco(inputPath, bmp);
                case "pdf":
                    return SaveImageAsPdf(inputPath, bmp);
                default:
                    return null;
            }
        }

        if (IsTextFile(inputPath))
        {
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
            string content = File.ReadAllText(inputPath);

            if (targetKey == "csv_to_json")
            {
                string json = ConvertCsvToJson(content);
                string outPath = GetUniqueOutputPath(inputPath, ".json");
                File.WriteAllText(outPath, json, new UTF8Encoding(false));
                return outPath;
            }
            if (targetKey == "json_to_csv")
            {
                string csv = ConvertJsonToCsv(content);
                string outPath = GetUniqueOutputPath(inputPath, ".csv");
                File.WriteAllText(outPath, csv, new UTF8Encoding(true));
                return outPath;
            }
            if (targetKey == "format_json")
            {
                using var doc = JsonDocument.Parse(content);
                string pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                string outPath = GetUniqueOutputPath(inputPath, ".json", "_formatted");
                File.WriteAllText(outPath, pretty, new UTF8Encoding(false));
                return outPath;
            }
            if (targetKey == "txt")
            {
                string outPath = GetUniqueOutputPath(inputPath, ".txt");
                File.WriteAllText(outPath, content, Encoding.UTF8);
                return outPath;
            }
            if (targetKey == "html")
            {
                string outPath = GetUniqueOutputPath(inputPath, ".html");
                string html = BuildRichHtmlDocument(Path.GetFileNameWithoutExtension(inputPath), ext, content);
                File.WriteAllText(outPath, html, Encoding.UTF8);
                return outPath;
            }
            if (targetKey == "pdf")
            {
                var renderedPage = RenderTextToBitmap(Path.GetFileName(inputPath), content);
                return SaveImageAsPdf(inputPath, renderedPage);
            }
        }

        return null;
    }

    private static BitmapSource? LoadBitmap(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault();
        if (frame == null) return null;
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource FlattenTransparency(BitmapSource source, Color bg)
    {
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            dc.DrawImage(source, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource ResizeBitmap(BitmapSource source, int targetW, int targetH)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            dc.DrawImage(source, new Rect(0, 0, targetW, targetH));
        }
        var rtb = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource CropSquareImage(BitmapSource source)
    {
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        int side = Math.Min(w, h);
        var cropped = new CroppedBitmap(source, new Int32Rect((w - side) / 2, (h - side) / 2, side, side));
        cropped.Freeze();
        return cropped;
    }

    private static BitmapSource ConvertToGrayscale(BitmapSource source)
    {
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        byte[] px = new byte[w * h * 4];
        source.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte lum = (byte)((px[i + 2] * 77 + px[i + 1] * 150 + px[i] * 29) >> 8);
            px[i] = lum;
            px[i + 1] = lum;
            px[i + 2] = lum;
        }
        var wb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        wb.Freeze();
        return wb;
    }

    private static string SaveEncodedImage(string inputPath, string ext, BitmapSource bmp, BitmapEncoder encoder, string suffix = "")
    {
        string outPath = GetUniqueOutputPath(inputPath, ext, suffix);
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
        return outPath;
    }

    private static string CompressImage(string inputPath, BitmapSource bmp, out BitmapSource compressedBmp)
    {
        long origBytes = File.Exists(inputPath) ? new FileInfo(inputPath).Length : long.MaxValue;
        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        if (w > 2048 || h > 2048)
        {
            double scale = 2048.0 / Math.Max(w, h);
            w = Math.Max(1, (int)Math.Round(w * scale));
            h = Math.Max(1, (int)Math.Round(h * scale));
            bmp = ResizeBitmap(bmp, w, h);
        }

        var currentBmp = FlattenTransparency(bmp, Colors.White);
        byte[]? bestBytes = null;

        int[] qualities = { 72, 58, 45, 34 };
        foreach (int q in qualities)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = q };
            enc.Frames.Add(BitmapFrame.Create(currentBmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            bestBytes = ms.ToArray();
            if (bestBytes.Length < origBytes * 0.78)
            {
                break;
            }
        }

        // Если исходник уже был экстремально пережат, уменьшаем разрешение на 25%, чтобы гарантированно выиграть в весе
        if (bestBytes != null && bestBytes.Length >= origBytes * 0.95 && w > 320 && h > 320)
        {
            int nw = Math.Max(160, (int)Math.Round(w * 0.72));
            int nh = Math.Max(160, (int)Math.Round(h * 0.72));
            currentBmp = FlattenTransparency(ResizeBitmap(bmp, nw, nh), Colors.White);
            var enc = new JpegBitmapEncoder { QualityLevel = 52 };
            enc.Frames.Add(BitmapFrame.Create(currentBmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            bestBytes = ms.ToArray();
        }

        compressedBmp = currentBmp;
        string outPath = GetUniqueOutputPath(inputPath, ".jpg", "_compressed");
        File.WriteAllBytes(outPath, bestBytes ?? Array.Empty<byte>());
        return outPath;
    }

    private static string SaveAsIco(string inputPath, BitmapSource bmp)
    {
        string outPath = GetUniqueOutputPath(inputPath, ".ico");
        int[] sizes = { 256, 64, 32, 16 };
        var pngPayloads = new List<(int Size, byte[] Bytes)>();

        foreach (int sz in sizes)
        {
            var resized = ResizeBitmap(bmp, sz, sz);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(resized));
            using var ms = new MemoryStream();
            enc.Save(ms);
            pngPayloads.Add((sz, ms.ToArray()));
        }

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)pngPayloads.Count);

        int offset = 6 + pngPayloads.Count * 16;
        foreach (var (sz, bytes) in pngPayloads)
        {
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)bytes.Length);
            bw.Write((uint)offset);
            offset += bytes.Length;
        }

        foreach (var (_, bytes) in pngPayloads)
        {
            bw.Write(bytes);
        }
        return outPath;
    }

    private static string SaveImageAsPdf(string inputPath, BitmapSource bmp)
    {
        string outPath = GetUniqueOutputPath(inputPath, ".pdf");
        WriteMultiPagePdf(outPath, new[] { bmp });
        return outPath;
    }

    private static string SaveImagesAsMultiPagePdf(IReadOnlyList<string> imageFiles, CancellationToken ct = default)
    {
        var bitmaps = new List<BitmapSource>();
        foreach (string f in imageFiles)
        {
            ct.ThrowIfCancellationRequested();
            var b = LoadBitmap(f);
            if (b != null) bitmaps.Add(b);
        }
        if (bitmaps.Count == 0) throw new InvalidOperationException("No valid images for PDF");

        string outPath = GetUniqueOutputPath(imageFiles[0], ".pdf", "_merged");
        try
        {
            WriteMultiPagePdf(outPath, bitmaps, ct);
            return outPath;
        }
        catch
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            throw;
        }
    }

    private static void WriteMultiPagePdf(string outPath, IReadOnlyList<BitmapSource> pages, CancellationToken ct = default)
    {
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();

        void WriteAscii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            fs.Write(b, 0, b.Length);
        }

        WriteAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        offsets.Add(fs.Position);
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int pageCount = pages.Count;
        var kidsBuilder = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
        {
            if (i > 0) kidsBuilder.Append(' ');
            kidsBuilder.Append($"{3 + i * 3} 0 R");
        }

        offsets.Add(fs.Position);
        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [{kidsBuilder}] /Count {pageCount} >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var flat = FlattenTransparency(pages[i], Colors.White);
            int imgW = flat.PixelWidth;
            int imgH = flat.PixelHeight;

            var jpegEnc = new JpegBitmapEncoder { QualityLevel = 90 };
            jpegEnc.Frames.Add(BitmapFrame.Create(flat));
            using var jpgMs = new MemoryStream();
            jpegEnc.Save(jpgMs);
            byte[] jpgBytes = jpgMs.ToArray();

            double pageW = imgW * 0.75;
            double pageH = imgH * 0.75;
            if (pageW > 842 || pageH > 842)
            {
                double s = 842.0 / Math.Max(pageW, pageH);
                pageW *= s;
                pageH *= s;
            }

            string pwStr = pageW.ToString("0.##", CultureInfo.InvariantCulture);
            string phStr = pageH.ToString("0.##", CultureInfo.InvariantCulture);

            int pageObjId = 3 + i * 3;
            int imgObjId = 4 + i * 3;
            int cntObjId = 5 + i * 3;

            offsets.Add(fs.Position);
            WriteAscii($"{pageObjId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pwStr} {phStr}] /Resources << /XObject << /Im{i} {imgObjId} 0 R >> >> /Contents {cntObjId} 0 R >>\nendobj\n");

            offsets.Add(fs.Position);
            WriteAscii($"{imgObjId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpgBytes.Length} >>\nstream\n");
            fs.Write(jpgBytes, 0, jpgBytes.Length);
            WriteAscii("\nendstream\nendobj\n");

            string contentCmd = $"q\n{pwStr} 0 0 {phStr} 0 0 cm\n/Im{i} Do\nQ\n";
            byte[] contentBytes = Encoding.ASCII.GetBytes(contentCmd);
            offsets.Add(fs.Position);
            WriteAscii($"{cntObjId} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            fs.Write(contentBytes, 0, contentBytes.Length);
            WriteAscii("endstream\nendobj\n");
        }

        long xrefPos = fs.Position;
        WriteAscii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets)
        {
            WriteAscii($"{off:D10} 00000 n \n");
        }
        WriteAscii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
    }

    private static async Task<string> RunWindowsOcrAsync(string imagePath)
    {
        var bmp = LoadBitmap(imagePath);
        if (bmp == null) return "";

        var pngEnc = new PngBitmapEncoder();
        pngEnc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        pngEnc.Save(ms);
        ms.Position = 0;

        using var ras = ms.AsRandomAccessStream();
        var winDecoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        using var softBmp = await winDecoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ru"))
                     ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en"));

        if (engine == null) return "";
        var result = await engine.RecognizeAsync(softBmp);
        if (result == null) return "";

        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            sb.AppendLine(line.Text);
        }
        return sb.ToString().Trim();
    }

    // ==================== АППАРАТНЫЙ ВИДЕО-ДВИЖОК WINDOWS MEDIA FOUNDATION ====================

    private static async Task<Windows.Storage.StorageFile> CreateEmptyWinRtFileAsync(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath) ?? "";
        string fileName = Path.GetFileName(outputPath);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(dir);
        return await folder.CreateFileAsync(fileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
    }

    private static async Task<string?> ExtractAudioFromVideoAsync(string inputPath, Action<double>? onProgress, CancellationToken ct = default)
    {
        var inSf = await Windows.Storage.StorageFile.GetFileFromPathAsync(inputPath);
        var transcoder = new Windows.Media.Transcoding.MediaTranscoder
        {
            HardwareAccelerationEnabled = true
        };

        string outMp3 = GetUniqueOutputPath(inputPath, ".mp3");
        var outSf = await CreateEmptyWinRtFileAsync(outMp3);
        var mp3Profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp3(
            Windows.Media.MediaProperties.AudioEncodingQuality.High);

        var prep = await transcoder.PrepareFileTranscodeAsync(inSf, outSf, mp3Profile);
        if (prep.CanTranscode)
        {
            try
            {
                var op = prep.TranscodeAsync();
                using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
                op.Progress = (_, pct) => onProgress?.Invoke(pct);
                await op;
                ct.ThrowIfCancellationRequested();
                return outMp3;
            }
            catch
            {
                try { File.Delete(outMp3); } catch { }
                throw;
            }
        }

        try { File.Delete(outMp3); } catch { }

        string outM4a = GetUniqueOutputPath(inputPath, ".m4a");
        var outM4aSf = await CreateEmptyWinRtFileAsync(outM4a);
        var m4aProfile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(
            Windows.Media.MediaProperties.AudioEncodingQuality.High);

        var prepM4a = await transcoder.PrepareFileTranscodeAsync(inSf, outM4aSf, m4aProfile);
        if (prepM4a.CanTranscode)
        {
            try
            {
                var op = prepM4a.TranscodeAsync();
                using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
                op.Progress = (_, pct) => onProgress?.Invoke(pct);
                await op;
                ct.ThrowIfCancellationRequested();
                return outM4a;
            }
            catch
            {
                try { File.Delete(outM4a); } catch { }
                throw;
            }
        }

        try { File.Delete(outM4a); } catch { }
        return null;
    }

    private static async Task<string?> TranscodeVideoAsync(string inputPath, bool compress720p, bool stripAudio, Action<double>? onProgress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long srcBytes = File.Exists(inputPath) ? new FileInfo(inputPath).Length : 0;
        var inSf = await Windows.Storage.StorageFile.GetFileFromPathAsync(inputPath);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(inSf);
        double durationSec = Math.Max(0.5, clip.OriginalDuration.TotalSeconds);
        double srcTotalBps = srcBytes > 0 ? (srcBytes * 8.0) / durationSec : 2_500_000.0;

        var vProps = clip.GetVideoEncodingProperties();
        int srcW = (int)(vProps?.Width ?? 1280);
        int srcH = (int)(vProps?.Height ?? 720);
        if (srcW <= 0) srcW = 1280;
        if (srcH <= 0) srcH = 720;

        string suffix = compress720p ? "_compressed" : (stripAudio ? "_muted" : "");
        string outPath = GetUniqueOutputPath(inputPath, ".mp4", suffix);
        var outSf = await CreateEmptyWinRtFileAsync(outPath);

        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(
            Windows.Media.MediaProperties.VideoEncodingQuality.HD720p);

        int targetW = srcW;
        int targetH = srcH;

        if (compress720p)
        {
            // Целевой общий битрейт = ~42% от исходного (чтобы гарантированно уменьшить размер файла в 2–2.5 раза)
            double targetTotalBps = Math.Min(srcTotalBps * 0.42, 1_350_000.0);
            uint audioBps = targetTotalBps < 260_000 ? 64_000u : 96_000u;
            uint videoBps = (uint)Math.Clamp(targetTotalBps - audioBps, 95_000.0, 1_250_000.0);

            int maxDim = videoBps < 400_000 ? 854 : 1280;
            int longest = Math.Max(srcW, srcH);
            if (longest > maxDim)
            {
                double scale = maxDim / (double)longest;
                targetW = (int)Math.Round(srcW * scale);
                targetH = (int)Math.Round(srcH * scale);
            }

            if (profile.Video != null)
            {
                profile.Video.Width = (uint)Math.Max(160, targetW & ~1);
                profile.Video.Height = (uint)Math.Max(160, targetH & ~1);
                profile.Video.Bitrate = videoBps;
            }
            if (profile.Audio != null)
            {
                profile.Audio.Bitrate = audioBps;
            }
        }
        else
        {
            // Для обычного перевода в MP4 или удаления звука сохраняем исходное разрешение и не раздуваем битрейт выше исходника
            uint maxVideoBps = (uint)Math.Clamp(srcTotalBps * 0.85, 250_000.0, 6_000_000.0);
            if (profile.Video != null)
            {
                profile.Video.Width = (uint)Math.Max(160, targetW & ~1);
                profile.Video.Height = (uint)Math.Max(160, targetH & ~1);
                profile.Video.Bitrate = maxVideoBps;
            }
            if (stripAudio)
            {
                profile.Audio = null;
            }
            else if (profile.Audio != null)
            {
                profile.Audio.Bitrate = 128_000;
            }
        }

        var transcoder = new Windows.Media.Transcoding.MediaTranscoder
        {
            HardwareAccelerationEnabled = true
        };

        var prep = await transcoder.PrepareFileTranscodeAsync(inSf, outSf, profile);
        if (!prep.CanTranscode)
        {
            try { File.Delete(outPath); } catch { }
            return null;
        }

        try
        {
            var op = prep.TranscodeAsync();
            using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
            op.Progress = (_, pct) => onProgress?.Invoke(pct);
            await op;
            ct.ThrowIfCancellationRequested();
            return outPath;
        }
        catch
        {
            try { File.Delete(outPath); } catch { }
            throw;
        }
    }

    private static async Task<string?> MergeVideosAsMp4Async(IReadOnlyList<string> videoPaths, Action<double>? onProgress, CancellationToken ct = default)
    {
        var comp = new Windows.Media.Editing.MediaComposition();
        foreach (string path in videoPaths)
        {
            ct.ThrowIfCancellationRequested();
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(sf);
            comp.Clips.Add(clip);
        }

        if (comp.Clips.Count == 0) return null;

        string outPath = GetUniqueOutputPath(videoPaths[0], ".mp4", "_merged");
        var outSf = await CreateEmptyWinRtFileAsync(outPath);

        try
        {
            var op = comp.RenderToFileAsync(outSf, Windows.Media.Editing.MediaTrimmingPreference.Fast);
            using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
            op.Progress = (_, pct) => onProgress?.Invoke(pct);
            var reason = await op;
            ct.ThrowIfCancellationRequested();
            if (reason != Windows.Media.Transcoding.TranscodeFailureReason.None)
            {
                try { File.Delete(outPath); } catch { }
                return null;
            }
            return outPath;
        }
        catch
        {
            try { File.Delete(outPath); } catch { }
            throw;
        }
    }

    private static async Task<(string? OutputPath, BitmapSource? FrameBmp)> ExtractVideoFramePngAsync(string inputPath, Action<double>? onProgress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        onProgress?.Invoke(20);
        var inSf = await Windows.Storage.StorageFile.GetFileFromPathAsync(inputPath);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(inSf);
        var comp = new Windows.Media.Editing.MediaComposition();
        comp.Clips.Add(clip);

        double totalSec = comp.Duration.TotalSeconds;
        double sampleSec = totalSec > 1.2 ? Math.Min(totalSec * 0.15, totalSec - 0.1) : Math.Max(0.0, totalSec * 0.5);

        using var thumbStream = await comp.GetThumbnailAsync(
            TimeSpan.FromSeconds(sampleSec),
            0,
            0,
            Windows.Media.Editing.VideoFramePrecision.NearestFrame);

        ct.ThrowIfCancellationRequested();
        onProgress?.Invoke(75);
        using var netStream = thumbStream.AsStreamForRead();
        using var ms = new MemoryStream();
        await netStream.CopyToAsync(ms, ct);
        ms.Position = 0;

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();

        string outPng = SaveEncodedImage(inputPath, ".png", bi, new PngBitmapEncoder(), "_frame");
        onProgress?.Invoke(100);
        return (outPng, bi);
    }

    private static async Task<string?> ConvertVideoToAnimatedGifAsync(string inputPath, Action<double>? onProgress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var inSf = await Windows.Storage.StorageFile.GetFileFromPathAsync(inputPath);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(inSf);
        var comp = new Windows.Media.Editing.MediaComposition();
        comp.Clips.Add(clip);

        double totalSec = Math.Min(comp.Duration.TotalSeconds, 8.0);
        if (totalSec <= 0.05) totalSec = 1.0;

        var props = clip.GetVideoEncodingProperties();
        int srcW = (int)(props?.Width ?? 640);
        int srcH = (int)(props?.Height ?? 360);
        if (srcW <= 0) srcW = 640;
        if (srcH <= 0) srcH = 360;

        int targetW = Math.Min(480, srcW);
        int targetH = Math.Max(64, (int)Math.Round(srcH * (targetW / (double)srcW)));

        int fps = 10;
        int frameCount = Math.Clamp((int)Math.Round(totalSec * fps), 4, 80);

        var encoder = new GifBitmapEncoder();
        for (int i = 0; i < frameCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            double t = Math.Min(comp.Duration.TotalSeconds - 0.02, (i / (double)fps));
            if (t < 0) t = 0;

            using var thumb = await comp.GetThumbnailAsync(
                TimeSpan.FromSeconds(t),
                targetW,
                targetH,
                Windows.Media.Editing.VideoFramePrecision.NearestFrame);

            using var netStream = thumb.AsStreamForRead();
            using var frameMs = new MemoryStream();
            await netStream.CopyToAsync(frameMs, ct);
            frameMs.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = frameMs;
            bi.EndInit();
            bi.Freeze();

            encoder.Frames.Add(BitmapFrame.Create(bi));
            onProgress?.Invoke(10.0 + (i + 1) * 80.0 / frameCount);
        }

        ct.ThrowIfCancellationRequested();
        using var rawGifStream = new MemoryStream();
        encoder.Save(rawGifStream);
        byte[] patchedGif = PatchAnimatedGifDelayAndLoop(rawGifStream.ToArray(), delayCentiseconds: 10);

        string outGif = GetUniqueOutputPath(inputPath, ".gif");
        await File.WriteAllBytesAsync(outGif, patchedGif, ct);
        onProgress?.Invoke(100);
        return outGif;
    }

    private static byte[] PatchAnimatedGifDelayAndLoop(byte[] rawGif, ushort delayCentiseconds = 10)
    {
        if (rawGif.Length < 14) return rawGif;

        int pos = 13;
        byte packed = rawGif[10];
        if ((packed & 0x80) != 0)
        {
            int gctSize = 3 * (1 << ((packed & 0x07) + 1));
            pos += gctSize;
        }
        if (pos >= rawGif.Length) return rawGif;

        using var outMs = new MemoryStream(rawGif.Length + 256);
        outMs.Write(rawGif, 0, pos);

        // Вставляем расширение бесконечного цикла NETSCAPE2.0
        ReadOnlySpan<byte> netscapeLoop = new byte[]
        {
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
            (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00, 0x00
        };
        outMs.Write(netscapeLoop);

        byte delayLo = (byte)(delayCentiseconds & 0xFF);
        byte delayHi = (byte)((delayCentiseconds >> 8) & 0xFF);

        // Патчим задержку каждого кадра в блоках Graphic Control Extension (21 F9 04)
        for (int i = pos; i < rawGif.Length; i++)
        {
            if (i + 7 < rawGif.Length &&
                rawGif[i] == 0x21 &&
                rawGif[i + 1] == 0xF9 &&
                rawGif[i + 2] == 0x04 &&
                rawGif[i + 7] == 0x00)
            {
                rawGif[i + 4] = delayLo;
                rawGif[i + 5] = delayHi;
            }
        }

        outMs.Write(rawGif, pos, rawGif.Length - pos);
        return outMs.ToArray();
    }

    private static string CreateZipFromItems(IReadOnlyList<string> items, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        string first = items[0];
        string outZip = GetUniqueOutputPath(first, ".zip");

        var entriesToPack = new List<(string FullPath, string EntryName, long Size)>();
        foreach (string item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(item))
            {
                long sz = new FileInfo(item).Length;
                entriesToPack.Add((item, Path.GetFileName(item), sz));
            }
            else if (Directory.Exists(item))
            {
                string rootName = items.Count == 1
                    ? ""
                    : Path.GetFileName(item.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                foreach (string subFile in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    string rel = Path.GetRelativePath(item, subFile);
                    string entryName = string.IsNullOrEmpty(rootName) ? rel : Path.Combine(rootName, rel);
                    long sz = 0;
                    try { sz = new FileInfo(subFile).Length; } catch { }
                    entriesToPack.Add((subFile, entryName.Replace('\\', '/'), sz));
                }
            }
        }

        long totalBytes = Math.Max(1L, entriesToPack.Sum(e => e.Size));
        long processedBytes = 0;
        int lastReportedPct = 0;
        byte[] buffer = new byte[81920];

        try
        {
            using var fs = new FileStream(outZip, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

            foreach (var (fullPath, entryName, _) in entriesToPack)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(fullPath)) continue;

                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                try { entry.LastWriteTime = File.GetLastWriteTime(fullPath); } catch { }

                using var srcStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dstStream = entry.Open();

                int read;
                while ((read = srcStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dstStream.Write(buffer, 0, read);
                    processedBytes += read;

                    int pct = Math.Clamp((int)Math.Round((processedBytes * 96.0) / totalBytes) + 2, 2, 98);
                    if (pct > lastReportedPct)
                    {
                        lastReportedPct = pct;
                        onProgress?.Invoke(pct);
                    }
                }
            }
            return outZip;
        }
        catch
        {
            try { if (File.Exists(outZip)) File.Delete(outZip); } catch { }
            throw;
        }
    }

    private static void ExtractZipToDirectoryWithCancel(string archivePath, string destDir, Action<double>? onProgress, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

            long totalBytes = Math.Max(1L, archive.Entries.Sum(e => Math.Max(0L, e.Length)));
            long extractedBytes = 0;
            int lastReportedPct = 0;
            byte[] buffer = new byte[81920];
            string fullDestRoot = Path.GetFullPath(destDir);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string targetPath = Path.GetFullPath(Path.Combine(fullDestRoot, entry.FullName));
                if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase)) continue;

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                string? parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                using var entryStream = entry.Open();
                using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

                int read;
                while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    outStream.Write(buffer, 0, read);
                    extractedBytes += read;

                    int pct = Math.Clamp((int)Math.Round((extractedBytes * 96.0) / totalBytes) + 2, 2, 98);
                    if (pct > lastReportedPct)
                    {
                        lastReportedPct = pct;
                        onProgress?.Invoke(pct);
                    }
                }
            }
        }
        catch
        {
            try { if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true); } catch { }
            throw;
        }
    }

    private static string GetUniqueDirectoryPath(string archivePath)
    {
        string dir = Path.GetDirectoryName(archivePath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(archivePath);
        string candidate = Path.Combine(dir, stem);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;

        for (int i = 1; i < 1000; i++)
        {
            string numbered = Path.Combine(dir, $"{stem} ({i})");
            if (!Directory.Exists(numbered) && !File.Exists(numbered)) return numbered;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.Now:HHmmss}");
    }

    private static List<string[]> ParseCsvRows(string csvText)
    {
        var lines = csvText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new List<string[]>();

        char sep = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var rows = new List<string[]>(lines.Length);

        foreach (string line in lines)
        {
            var fields = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == sep && !inQuotes)
                {
                    fields.Add(cur.ToString().Trim());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            fields.Add(cur.ToString().Trim());
            rows.Add(fields.ToArray());
        }
        return rows;
    }

    private static string ConvertCsvToJson(string csvText)
    {
        var rows = ParseCsvRows(csvText);
        if (rows.Count == 0) return "[]";

        string[] headers = rows[0];
        var list = new List<Dictionary<string, object?>>();

        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var dict = new Dictionary<string, object?>();
            for (int c = 0; c < headers.Length; c++)
            {
                string key = string.IsNullOrWhiteSpace(headers[c]) ? $"col_{c + 1}" : headers[c];
                string val = c < row.Length ? row[c] : "";
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lVal))
                    dict[key] = lVal;
                else if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dVal))
                    dict[key] = dVal;
                else if (bool.TryParse(val, out bool bVal))
                    dict[key] = bVal;
                else
                    dict[key] = val;
            }
            list.Add(dict);
        }

        return JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string ConvertJsonToCsv(string jsonText)
    {
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        var elements = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray()) elements.Add(el);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            elements.Add(root);
        }

        if (elements.Count == 0) return "";

        var columns = new List<string>();
        foreach (var el in elements)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (!columns.Contains(prop.Name)) columns.Add(prop.Name);
                }
            }
        }

        static string EscapeCsv(string s)
        {
            if (s.Contains(',') || s.Contains(';') || s.Contains('"') || s.Contains('\n'))
            {
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            return s;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(EscapeCsv)));

        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var vals = new List<string>(columns.Count);
            foreach (string col in columns)
            {
                if (el.TryGetProperty(col, out var v))
                {
                    string str = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString() ?? "",
                        JsonValueKind.Null or JsonValueKind.Undefined => "",
                        _ => v.ToString()
                    };
                    vals.Add(EscapeCsv(str));
                }
                else
                {
                    vals.Add("");
                }
            }
            sb.AppendLine(string.Join(",", vals));
        }
        return sb.ToString();
    }

    private static string BuildRichHtmlDocument(string rawTitle, string ext, string content)
    {
        string title = System.Net.WebUtility.HtmlEncode(rawTitle);
        string styles = """
            body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:900px;margin:40px auto;padding:0 24px;line-height:1.65;background:#101014;color:#f2f2f7;}
            h1,h2,h3{color:#ffffff;border-bottom:1px solid #26262e;padding-bottom:6px;margin-top:24px;}
            pre,code{font-family:'Cascadia Code',Consolas,monospace;background:#1a1a22;border-radius:8px;}
            pre{padding:16px;overflow-x:auto;border:1px solid #2c2c38;}
            code{padding:2px 6px;color:#38bdf8;}
            table{border-collapse:collapse;width:100%;margin:20px 0;background:#16161d;border-radius:10px;overflow:hidden;}
            th,td{border:1px solid #2a2a35;padding:10px 14px;text-align:left;font-size:14px;}
            th{background:#22222e;color:#38bdf8;font-weight:600;}
            tr:nth-child(even){background:#1a1a23;}
            """;

        if (ext == ".csv")
        {
            var rows = ParseCsvRows(content);
            var sb = new StringBuilder();
            sb.AppendLine("<table>");
            for (int r = 0; r < rows.Count; r++)
            {
                sb.AppendLine("<tr>");
                string tag = r == 0 ? "th" : "td";
                foreach (string cell in rows[r])
                {
                    sb.AppendLine($"<{tag}>{System.Net.WebUtility.HtmlEncode(cell)}</{tag}>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
            return $"<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>{title}</title><style>{styles}</style></head><body><h1>📊 {title}</h1>{sb}</body></html>";
        }

        if (ext == ".md")
        {
            var sb = new StringBuilder();
            bool inCode = false;
            foreach (string rawLine in content.Replace("\r\n", "\n").Split('\n'))
            {
                if (rawLine.TrimStart().StartsWith("```"))
                {
                    sb.AppendLine(inCode ? "</code></pre>" : "<pre><code>");
                    inCode = !inCode;
                    continue;
                }
                if (inCode)
                {
                    sb.AppendLine(System.Net.WebUtility.HtmlEncode(rawLine));
                    continue;
                }

                string line = System.Net.WebUtility.HtmlEncode(rawLine);
                line = Regex.Replace(line, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
                line = Regex.Replace(line, @"`(.+?)`", "<code>$1</code>");

                if (line.StartsWith("### ")) sb.AppendLine($"<h3>{line[4..]}</h3>");
                else if (line.StartsWith("## ")) sb.AppendLine($"<h2>{line[3..]}</h2>");
                else if (line.StartsWith("# ")) sb.AppendLine($"<h1>{line[2..]}</h1>");
                else if (line.StartsWith("- ") || line.StartsWith("* ")) sb.AppendLine($"<li>{line[2..]}</li>");
                else if (string.IsNullOrWhiteSpace(line)) sb.AppendLine("<br/>");
                else sb.AppendLine($"<p>{line}</p>");
            }
            if (inCode) sb.AppendLine("</code></pre>");
            return $"<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>{title}</title><style>{styles}</style></head><body>{sb}</body></html>";
        }

        string escaped = System.Net.WebUtility.HtmlEncode(content);
        return $"<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>{title}</title><style>{styles}</style></head><body><h1>{title}</h1><pre>{escaped}</pre></body></html>";
    }

    private static BitmapSource RenderTextToBitmap(string title, string text)
    {
        int w = 1240;
        int margin = 72;
        string clipped = text.Length > 8000 ? text[..8000] + "\n\n[...]" : text;

        var formattedTitle = new FormattedText(
            title,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            28,
            Brushes.Black,
            1.0)
        {
            MaxTextWidth = w - margin * 2
        };

        var formattedBody = new FormattedText(
            clipped,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            16,
            new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            1.0)
        {
            MaxTextWidth = w - margin * 2
        };

        int h = Math.Clamp((int)Math.Ceiling(margin * 2 + formattedTitle.Height + 28 + formattedBody.Height), 1754, 4800);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(formattedTitle, new Point(margin, margin));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(220, 220, 225)), 1.5),
                new Point(margin, margin + formattedTitle.Height + 12),
                new Point(w - margin, margin + formattedTitle.Height + 12));
            dc.DrawText(formattedBody, new Point(margin, margin + formattedTitle.Height + 28));
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public static string GetUniqueOutputPath(string inputPath, string newExtension, string suffix = "")
    {
        string dir = Path.GetDirectoryName(inputPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(inputPath) + suffix;
        string candidate = Path.Combine(dir, stem + newExtension);
        if (!File.Exists(candidate) && !string.Equals(candidate, inputPath, StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        for (int i = 1; i < 1000; i++)
        {
            string numbered = Path.Combine(dir, $"{stem} ({i}){newExtension}");
            if (!File.Exists(numbered)) return numbered;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.Now:HHmmss}{newExtension}");
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        return $"{mb:0.##} MB";
    }

    // ==================== АВТОНОМНЫЙ КОДИРОВЩИК WEBP (VP8L RFC 9649) ====================

    private struct Vp8lToken
    {
        public bool IsMatch;
        public byte G, R, B, A;
        public ushort LenSym, DistSym;
        public byte LenExtraBits, DistExtraBits;
        public int LenExtraVal, DistExtraVal;
    }

    private sealed class Vp8lBitWriter
    {
        private readonly MemoryStream _ms = new(65536);
        private ulong _buf;
        private int _bits;

        public void WriteBits(uint value, int numBits)
        {
            if (numBits <= 0) return;
            _buf |= ((ulong)value & ((1UL << numBits) - 1UL)) << _bits;
            _bits += numBits;
            while (_bits >= 8)
            {
                _ms.WriteByte((byte)(_buf & 0xFF));
                _buf >>= 8;
                _bits -= 8;
            }
        }

        public byte[] ToArray()
        {
            if (_bits > 0)
            {
                _ms.WriteByte((byte)(_buf & 0xFF));
                _buf = 0;
                _bits = 0;
            }
            return _ms.ToArray();
        }
    }

    public static string SaveAsWebp(string inputPath, BitmapSource bmp)
    {
        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        if (w > 16383 || h > 16383)
        {
            double scale = 16383.0 / Math.Max(w, h);
            w = Math.Max(1, (int)Math.Round(w * scale));
            h = Math.Max(1, (int)Math.Round(h * scale));
            bmp = ResizeBitmap(bmp, w, h);
        }

        if (bmp.Format != PixelFormats.Bgra32)
        {
            bmp = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        }

        int n = w * h;
        byte[] raw = new byte[n * 4];
        bmp.CopyPixels(raw, w * 4, 0);

        uint[] pixels = new uint[n];
        bool hasAlpha = false;
        for (int i = 0; i < n; i++)
        {
            int off = i * 4;
            byte b = raw[off];
            byte g = raw[off + 1];
            byte r = raw[off + 2];
            byte a = raw[off + 3];
            if (a < 255) hasAlpha = true;

            byte sr = (byte)(r - g);
            byte sb = (byte)(b - g);
            pixels[i] = ((uint)a << 24) | ((uint)sr << 16) | ((uint)g << 8) | sb;
        }

        int[] freqG = new int[280];
        int[] freqR = new int[256];
        int[] freqB = new int[256];
        int[] freqA = new int[256];
        int[] freqD = new int[40];

        var tokens = new List<Vp8lToken>(n);
        int[] hashTable = new int[65536];
        Array.Fill(hashTable, -1);

        int idx = 0;
        while (idx < n)
        {
            int bestLen = 0;
            int bestDist = 1;

            int maxLen = Math.Min(4096, n - idx);
            if (maxLen >= 3)
            {
                if (idx > 0 && pixels[idx] == pixels[idx - 1])
                {
                    int len = 1;
                    uint p = pixels[idx];
                    while (len < maxLen && pixels[idx + len] == p) len++;
                    if (len >= 3)
                    {
                        bestLen = len;
                        bestDist = 1;
                    }
                }

                uint hVal = ((pixels[idx] * 2654435761u) ^ (pixels[idx + 1] * 2246822519u)) >> 16;
                int prev = hashTable[hVal];
                hashTable[hVal] = idx;

                if (prev >= 0 && idx - prev <= 32768 && bestLen < 64)
                {
                    int len = 0;
                    while (len < maxLen && pixels[prev + len] == pixels[idx + len]) len++;
                    if (len > bestLen && len >= 3)
                    {
                        bestLen = len;
                        bestDist = idx - prev;
                    }
                }
            }

            if (bestLen >= 3)
            {
                int distCode = bestDist + 120;
                GetVp8lPrefixCode(bestLen, out int lenSym, out int lenExtraBits, out int lenExtraVal);
                GetVp8lPrefixCode(distCode, out int distSym, out int distExtraBits, out int distExtraVal);

                freqG[256 + lenSym]++;
                freqD[distSym]++;

                tokens.Add(new Vp8lToken
                {
                    IsMatch = true,
                    LenSym = (ushort)(256 + lenSym),
                    LenExtraBits = (byte)lenExtraBits,
                    LenExtraVal = lenExtraVal,
                    DistSym = (ushort)distSym,
                    DistExtraBits = (byte)distExtraBits,
                    DistExtraVal = distExtraVal
                });

                int end = idx + bestLen;
                idx++;
                while (idx < end)
                {
                    if (idx + 1 < n)
                    {
                        uint hv = ((pixels[idx] * 2654435761u) ^ (pixels[idx + 1] * 2246822519u)) >> 16;
                        hashTable[hv] = idx;
                    }
                    idx++;
                }
            }
            else
            {
                uint px = pixels[idx];
                byte sb = (byte)(px & 0xFF);
                byte g = (byte)((px >> 8) & 0xFF);
                byte sr = (byte)((px >> 16) & 0xFF);
                byte a = (byte)((px >> 24) & 0xFF);

                freqG[g]++;
                freqR[sr]++;
                freqB[sb]++;
                freqA[a]++;

                tokens.Add(new Vp8lToken
                {
                    IsMatch = false,
                    G = g,
                    R = sr,
                    B = sb,
                    A = a
                });
                idx++;
            }
        }

        var bw = new Vp8lBitWriter();
        bw.WriteBits(0x2F, 8);
        bw.WriteBits((uint)(w - 1), 14);
        bw.WriteBits((uint)(h - 1), 14);
        bw.WriteBits(hasAlpha ? 1u : 0u, 1);
        bw.WriteBits(0, 3);

        bw.WriteBits(1, 1);
        bw.WriteBits(2, 2);
        bw.WriteBits(0, 1);

        bw.WriteBits(0, 1);
        bw.WriteBits(0, 1);

        WriteVp8lPrefixTree(bw, freqG, 280, out uint[] codesG, out int[] lenG);
        WriteVp8lPrefixTree(bw, freqR, 256, out uint[] codesR, out int[] lenR);
        WriteVp8lPrefixTree(bw, freqB, 256, out uint[] codesB, out int[] lenB);
        WriteVp8lPrefixTree(bw, freqA, 256, out uint[] codesA, out int[] lenA);
        WriteVp8lPrefixTree(bw, freqD, 40, out uint[] codesD, out int[] lenD);

        foreach (ref readonly var tk in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tokens))
        {
            if (!tk.IsMatch)
            {
                bw.WriteBits(codesG[tk.G], lenG[tk.G]);
                bw.WriteBits(codesR[tk.R], lenR[tk.R]);
                bw.WriteBits(codesB[tk.B], lenB[tk.B]);
                bw.WriteBits(codesA[tk.A], lenA[tk.A]);
            }
            else
            {
                bw.WriteBits(codesG[tk.LenSym], lenG[tk.LenSym]);
                bw.WriteBits((uint)tk.LenExtraVal, tk.LenExtraBits);
                bw.WriteBits(codesD[tk.DistSym], lenD[tk.DistSym]);
                bw.WriteBits((uint)tk.DistExtraVal, tk.DistExtraBits);
            }
        }

        byte[] vp8lPayload = bw.ToArray();
        int pad = vp8lPayload.Length & 1;
        uint riffSize = (uint)(4 + 8 + vp8lPayload.Length + pad);

        string outPath = GetUniqueOutputPath(inputPath, ".webp");
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WEBP"));
        writer.Write(Encoding.ASCII.GetBytes("VP8L"));
        writer.Write((uint)vp8lPayload.Length);
        writer.Write(vp8lPayload);
        if (pad == 1) writer.Write((byte)0);

        return outPath;
    }

    private static void GetVp8lPrefixCode(int v, out int symbol, out int extraBits, out int extraVal)
    {
        if (v <= 4)
        {
            symbol = v - 1;
            extraBits = 0;
            extraVal = 0;
            return;
        }
        int val = v - 1;
        int highestBit = System.Numerics.BitOperations.Log2((uint)val);
        extraBits = highestBit - 1;
        int secondBit = (val >> extraBits) & 1;
        symbol = 2 * highestBit + secondBit;
        extraVal = val & ((1 << extraBits) - 1);
    }

    private static void WriteVp8lPrefixTree(Vp8lBitWriter bw, int[] freq, int alphabetSize, out uint[] revCodes, out int[] lengths)
    {
        revCodes = new uint[alphabetSize];
        lengths = new int[alphabetSize];

        var used = new List<int>();
        for (int i = 0; i < alphabetSize; i++)
        {
            if (freq[i] > 0) used.Add(i);
        }

        if (used.Count == 0)
        {
            used.Add(0);
            freq[0] = 1;
        }

        if (used.Count == 1 && used[0] < 256)
        {
            bw.WriteBits(1, 1);
            bw.WriteBits(0, 1);
            bw.WriteBits(1, 1);
            bw.WriteBits((uint)used[0], 8);
            lengths[used[0]] = 0;
            revCodes[used[0]] = 0;
            return;
        }

        if (used.Count == 2 && used[0] < 256 && used[1] < 256)
        {
            bw.WriteBits(1, 1);
            bw.WriteBits(1, 1);
            bw.WriteBits(1, 1);
            bw.WriteBits((uint)used[0], 8);
            bw.WriteBits((uint)used[1], 8);
            lengths[used[0]] = 1;
            revCodes[used[0]] = 0;
            lengths[used[1]] = 1;
            revCodes[used[1]] = 1;
            return;
        }

        if (used.Count == 1)
        {
            int dummy = used[0] == 0 ? 1 : 0;
            freq[dummy] = 1;
            used.Add(dummy);
        }

        BuildCanonicalCodes(freq, alphabetSize, 15, lengths, revCodes);

        int[] clFreq = new int[19];
        int distinctCl = 0;
        for (int i = 0; i < alphabetSize; i++)
        {
            int l = lengths[i];
            if (clFreq[l] == 0) distinctCl++;
            clFreq[l]++;
        }
        if (distinctCl < 2)
        {
            int extra = clFreq[0] == 0 ? 0 : 1;
            clFreq[extra] = 1;
        }

        int[] clLengths = new int[19];
        uint[] clRevCodes = new uint[19];
        BuildCanonicalCodes(clFreq, 19, 7, clLengths, clRevCodes);

        bw.WriteBits(0, 1);
        bw.WriteBits(19 - 4, 4);
        ReadOnlySpan<int> kOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        for (int i = 0; i < 19; i++)
        {
            bw.WriteBits((uint)clLengths[kOrder[i]], 3);
        }
        bw.WriteBits(0, 1);

        for (int i = 0; i < alphabetSize; i++)
        {
            int l = lengths[i];
            bw.WriteBits(clRevCodes[l], clLengths[l]);
        }
    }

    private static void BuildCanonicalCodes(int[] freq, int alphabetSize, int maxBits, int[] lengths, uint[] revCodes)
    {
        var active = new List<int>();
        for (int i = 0; i < alphabetSize; i++)
        {
            if (freq[i] > 0) active.Add(i);
        }

        int m = active.Count;
        if (m == 1)
        {
            lengths[active[0]] = 1;
            revCodes[active[0]] = 0;
            return;
        }

        int totalNodes = 2 * m - 1;
        int[] parent = new int[totalNodes];
        Array.Fill(parent, -1);

        var pq = new PriorityQueue<int, long>(m);
        for (int i = 0; i < m; i++)
        {
            pq.Enqueue(i, ((long)freq[active[i]] << 16) | (uint)i);
        }

        int nextNode = m;
        while (pq.Count > 1)
        {
            pq.TryDequeue(out int a, out long pa);
            pq.TryDequeue(out int b, out long pb);
            long fa = pa >> 16;
            long fb = pb >> 16;
            int node = nextNode++;
            parent[a] = node;
            parent[b] = node;
            pq.Enqueue(node, ((fa + fb) << 16) | (uint)node);
        }

        int[] symDepths = new int[m];
        bool overflow = false;
        for (int i = 0; i < m; i++)
        {
            int d = 0;
            int cur = i;
            while (parent[cur] != -1)
            {
                d++;
                cur = parent[cur];
            }
            if (d > maxBits)
            {
                d = maxBits;
                overflow = true;
            }
            symDepths[i] = Math.Max(1, d);
        }

        if (overflow)
        {
            int[] order = Enumerable.Range(0, m).OrderByDescending(i => freq[active[i]]).ToArray();
            long target = 1L << maxBits;
            long kraft = 0;
            for (int i = 0; i < m; i++) kraft += 1L << (maxBits - symDepths[i]);

            while (kraft > target)
            {
                for (int k = m - 1; k >= 0 && kraft > target; k--)
                {
                    int idx = order[k];
                    if (symDepths[idx] < maxBits)
                    {
                        kraft -= 1L << (maxBits - symDepths[idx]);
                        symDepths[idx]++;
                        kraft += 1L << (maxBits - symDepths[idx]);
                    }
                }
            }

            while (kraft < target)
            {
                for (int k = 0; k < m && kraft < target; k++)
                {
                    int idx = order[k];
                    if (symDepths[idx] > 1)
                    {
                        kraft -= 1L << (maxBits - symDepths[idx]);
                        symDepths[idx]--;
                        kraft += 1L << (maxBits - symDepths[idx]);
                    }
                }
            }
        }

        int[] blCount = new int[maxBits + 1];
        for (int i = 0; i < m; i++)
        {
            int len = symDepths[i];
            lengths[active[i]] = len;
            blCount[len]++;
        }

        int[] nextCode = new int[maxBits + 1];
        int code = 0;
        for (int bits = 1; bits <= maxBits; bits++)
        {
            code = (code + blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        for (int s = 0; s < alphabetSize; s++)
        {
            int len = lengths[s];
            if (len > 0)
            {
                uint msb = (uint)(nextCode[len]++);
                revCodes[s] = ReverseBits(msb, len);
            }
        }
    }

    private static uint ReverseBits(uint v, int numBits)
    {
        uint r = 0;
        for (int i = 0; i < numBits; i++)
        {
            r = (r << 1) | (v & 1u);
            v >>= 1;
        }
        return r;
    }
}
