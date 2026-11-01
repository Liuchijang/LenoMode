using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LenoMode
{
    /// <summary>UI strings, English and Vietnamese. Elements whose Tag is a key get their text from here.</summary>
    static class L
    {
        public static bool Vi = true;

        static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {
            { "sec.thermal", new[] { "THERMAL MODE", "CHẾ ĐỘ NHIỆT" } },
            { "mode.0", new[] { "QUIET", "MÁT · ÊM" } },
            { "mode.1", new[] { "BALANCED", "CÂN BẰNG" } },
            { "mode.2", new[] { "PERFORMANCE", "HIỆU NĂNG" } },
            { "mode.0.desc", new[] { "Keeps temperatures low (about 50 °C). FPS drops under heavy load.", "Giữ nhiệt độ thấp (khoảng 50°C). Game nặng sẽ tụt FPS." } },
            { "mode.1.desc", new[] { "Balances performance and temperature.", "Cân bằng giữa hiệu năng và nhiệt độ." } },
            { "mode.2.desc", new[] { "Lets it reach about 70 °C for steady FPS. Fans run hard.", "Cho phép nóng tới khoảng 70°C để giữ FPS ổn định. Quạt quay mạnh." } },
            { "status.verified", new[] { "Read back from BIOS: {0}", "Đọc lại từ BIOS: {0}" } },
            { "status.live", new[] { "Running now (ITS): {0}", "Đang chạy (ITS): {0}" } },
            { "status.boot", new[] { "BIOS DEFAULT AT BOOT: {0}", "MẶC ĐỊNH KHI KHỞI ĐỘNG (BIOS): {0}" } },
            { "boot.save", new[] { "SAVE CURRENT AS BOOT DEFAULT", "LƯU CHẾ ĐỘ HIỆN TẠI LÀM MẶC ĐỊNH" } },
            { "status.vantage", new[] { "Lenovo Vantage is running and may switch the mode back", "Lenovo Vantage đang chạy, có thể tự đổi lại chế độ" } },
            { "status.applying", new[] { "Applying {0}…", "Đang đổi sang {0}…" } },
            { "status.error", new[] { "Failed: {0}", "Lỗi: {0}" } },
            { "status.unknown", new[] { "Reading BIOS…", "Đang đọc BIOS…" } },
            { "status.unsupported", new[] { "This device exposes neither Lenovo ITS nor SystemPerformanceMode.", "Máy không có Lenovo ITS lẫn SystemPerformanceMode." } },
            { "status.needadmin", new[] { "Run as administrator to read and change the BIOS thermal mode.", "Cần chạy với quyền Admin để đọc và đổi chế độ nhiệt trong BIOS." } },
            { "status.testlock", new[] { "Auto test is switching modes.", "Đang đo tự động, app tự đổi chế độ." } },

            { "sec.power", new[] { "WINDOWS POWER MODE", "POWER MODE CỦA WINDOWS" } },
            { "power.0", new[] { "EFFICIENCY", "TIẾT KIỆM" } },
            { "power.1", new[] { "BALANCED", "CÂN BẰNG" } },
            { "power.2", new[] { "BEST PERF", "HIỆU NĂNG" } },
            { "power.note", new[] { "Windows-side only, much weaker than the thermal mode. Applies to the current power source (AC or battery).",
                                    "Chỉ ở phía Windows, tác động yếu hơn nhiều so với chế độ nhiệt. Chỉ áp dụng cho nguồn đang dùng (sạc hoặc pin)." } },

            { "sec.app", new[] { "APP SETTINGS", "CÀI ĐẶT ỨNG DỤNG" } },
            { "sec.overlay", new[] { "IN-GAME OVERLAY", "OVERLAY TRONG GAME" } },
            { "ov.off", new[] { "○ OVERLAY OFF", "○ OVERLAY TẮT" } },
            { "ov.on", new[] { "● OVERLAY ON", "● OVERLAY BẬT" } },
            { "ov.corner.0", new[] { "↖ TOP LEFT", "↖ TRÊN TRÁI" } },
            { "ov.corner.1", new[] { "↗ TOP RIGHT", "↗ TRÊN PHẢI" } },
            { "ov.corner.2", new[] { "↙ BOTTOM LEFT", "↙ DƯỚI TRÁI" } },
            { "ov.corner.3", new[] { "↘ BOTTOM RIGHT", "↘ DƯỚI PHẢI" } },
            { "ov.row.show", new[] { "SHOW", "HIỂN THỊ" } },
            { "ov.row.layout", new[] { "LAYOUT", "BỐ CỤC" } },
            { "ov.row.size", new[] { "SIZE", "KÍCH THƯỚC" } },
            { "ov.row.bg", new[] { "BACKGROUND", "ĐỘ ĐẬM NỀN" } },
            { "ov.row.pos", new[] { "POSITION", "VỊ TRÍ" } },
            { "ov.move", new[] { "✥ DRAG…", "✥ KÉO THẢ…" } },
            { "ov.moved", new[] { "✥ CUSTOM SPOT", "✥ VỊ TRÍ TỰ CHỌN" } },
            { "ov.done", new[] { "✓ DONE", "✓ XONG" } },
            { "ov.movehint", new[] { "Drag the overlay to where you want it, then press DONE.", "Kéo overlay tới vị trí muốn đặt, rồi bấm XONG." } },
            { "ov.size.0", new[] { "SMALL", "NHỎ" } },
            { "ov.size.1", new[] { "MEDIUM", "VỪA" } },
            { "ov.size.2", new[] { "LARGE", "LỚN" } },
            { "ov.bg.0", new[] { "40%", "40%" } },
            { "ov.bg.1", new[] { "70%", "70%" } },
            { "ov.bg.2", new[] { "90%", "90%" } },
            { "ov.layout.0", new[] { "▤ PANEL", "▤ KHUNG" } },
            { "ov.layout.1", new[] { "▬ ONE LINE", "▬ THANH NGANG" } },
            { "ov.f.0", new[] { "MODE", "CHẾ ĐỘ" } },
            { "ov.f.1", new[] { "FPS", "FPS" } },
            { "ov.f.2", new[] { "1% LOW · MS", "1% LOW · MS" } },
            { "ov.f.3", new[] { "GPU", "GPU" } },
            { "ov.f.4", new[] { "CPU GHZ", "XUNG CPU" } },
            { "ov.f.5", new[] { "TEMP", "NHIỆT ĐỘ" } },
            { "ov.f.6", new[] { "POWER", "CÔNG SUẤT" } },
            { "ov.f.7", new[] { "RAM", "RAM" } },
            { "ov.f.8", new[] { "VRAM", "VRAM" } },
            { "ov.nofps", new[] { "no DirectX frames", "chưa có khung hình" } },
            { "ov.hotkeyfail", new[] { "Some hotkeys are taken by another app.", "Một số phím tắt đã bị app khác chiếm." } },
            { "autostart.off", new[] { "○ START WITH WINDOWS", "○ CHẠY CÙNG WINDOWS" } },
            { "autostart.on", new[] { "● START WITH WINDOWS", "● CHẠY CÙNG WINDOWS" } },
            { "admin.relaunch", new[] { "RESTART AS ADMIN", "MỞ LẠI VỚI QUYỀN ADMIN" } },
            { "tray.open", new[] { "Open LenoMode", "Mở LenoMode" } },
            { "tray.overlay", new[] { "Overlay", "Overlay" } },
            { "tray.exit", new[] { "Exit", "Thoát" } },
            { "tray.running", new[] { "RUNNING · {0}", "ĐANG CHẠY · {0}" } },
            { "tray.on", new[] { "ON", "BẬT" } },
            { "tray.off", new[] { "OFF", "TẮT" } },
            { "sub.tempadmin", new[] { "Needs admin (restart as admin)", "Cần quyền Admin (mở lại với quyền Admin)" } },
            { "sec.live", new[] { "LIVE MONITOR", "THEO DÕI TRỰC TIẾP" } },
            { "tile.fps", new[] { "FPS", "FPS" } },
            { "sub.fps", new[] { "1% low {0} · {1} ms · {2}", "1% low {0} · {1} ms · {2}" } },
            { "sub.nofps", new[] { "No DirectX frames from a game", "Chưa thấy khung hình DirectX từ game" } },
            { "sub.fpserr", new[] { "FPS unavailable: {0}", "Không đo được FPS: {0}" } },
            { "tile.game", new[] { "GAME GPU", "GPU CỦA GAME" } },
            { "tile.gpu", new[] { "GPU TOTAL", "GPU TỔNG" } },
            { "tile.cpu", new[] { "CPU LOAD", "TẢI CPU" } },
            { "tile.clock", new[] { "CPU CLOCK", "XUNG CPU" } },
            { "tile.temp", new[] { "TEMPERATURE", "NHIỆT ĐỘ" } },
            { "tile.power", new[] { "POWER", "CÔNG SUẤT" } },
            { "tile.ram", new[] { "RAM", "RAM" } },
            { "tile.vram", new[] { "GPU MEMORY", "BỘ NHỚ GPU" } },
            { "sub.nogame", new[] { "No app using the GPU", "Không có app dùng GPU" } },
            { "sub.gpu", new[] { "3D engine, all apps", "Engine 3D, mọi app" } },
            { "sub.cpu", new[] { "All cores, as in Task Manager", "Mọi nhân, như Task Manager" } },
            { "sub.limit", new[] { "Throttled {0}%", "Bị giới hạn {0}%" } },
            { "sub.temp", new[] { "ACPI sensor, not the CPU die", "Cảm biến ACPI, không phải lõi CPU" } },
            { "sub.ac", new[] { "On AC · battery {0}%", "Đang cắm sạc · pin {0}%" } },
            { "sub.battery", new[] { "Whole system, from battery · {0}%", "Cả máy, đo từ pin · {0}%" } },
            { "sub.ram", new[] { "{0} / {1} GB", "{0} / {1} GB" } },
            { "sub.vram", new[] { "Shared {0} · dedicated {1} GB", "Dùng chung {0} · riêng {1} GB" } },
            { "live.top", new[] { "TOP GPU APP: {0}", "APP DÙNG GPU NHIỀU NHẤT: {0}" } },

            { "sec.session", new[] { "MODE COMPARISON", "SO SÁNH CHẾ ĐỘ" } },
            { "rec.start", new[] { "● REC", "● GHI" } },
            { "rec.stop", new[] { "■ STOP", "■ DỪNG" } },
            { "test.start", new[] { "AUTO TEST", "ĐO TỰ ĐỘNG" } },
            { "test.cancel", new[] { "CANCEL TEST", "HUỶ ĐO" } },
            { "test.load.off", new[] { "CPU LOAD: OFF", "TẢI CPU: TẮT" } },
            { "test.load.on", new[] { "CPU LOAD: ON", "TẢI CPU: BẬT" } },
            { "test.dur", new[] { "{0}S / MODE", "{0}S / CHẾ ĐỘ" } },
            { "export", new[] { "EXPORT CSV", "XUẤT CSV" } },
            { "reset", new[] { "RESET", "XOÁ" } },
            { "test.hint", new[] { "REC logs averages under the current mode. AUTO TEST runs all 3 modes ({0}s each after a 10s warm-up), then restores the mode you had.",
                                   "GHI lưu số trung bình theo chế độ hiện tại. ĐO TỰ ĐỘNG chạy lần lượt 3 chế độ (mỗi chế độ {0}s sau 10s làm nóng), xong trả về chế độ ban đầu." } },
            { "test.warm", new[] { "Testing {0}: warm-up, {1}s", "Đang đo {0}: làm nóng, còn {1}s" } },
            { "test.run", new[] { "Testing {0}: recording, {1}s left", "Đang đo {0}: đang ghi, còn {1}s" } },
            { "test.done", new[] { "Test finished. Restored {0}.", "Đo xong. Đã trả về {0}." } },
            { "test.cancelled", new[] { "Test cancelled. Restored {0}.", "Đã huỷ đo. Đã trả về {0}." } },
            { "rec.on", new[] { "Recording under the current mode…", "Đang ghi theo chế độ hiện tại…" } },
            { "export.ok", new[] { "Saved {0}", "Đã lưu {0}" } },
            { "export.fail", new[] { "Could not save: {0}", "Không lưu được: {0}" } },
            { "tbl.mode", new[] { "MODE", "CHẾ ĐỘ" } },
            { "tbl.time", new[] { "TIME", "THỜI GIAN" } },
            { "tbl.fps", new[] { "FPS (1%)", "FPS (1%)" } },
            { "tbl.game", new[] { "GAME GPU", "GPU GAME" } },
            { "tbl.gpu", new[] { "GPU TOTAL", "GPU TỔNG" } },
            { "tbl.cpu", new[] { "CPU LOAD", "TẢI CPU" } },
            { "tbl.clock", new[] { "CPU CLOCK", "XUNG CPU" } },
            { "tbl.temp", new[] { "TEMP", "NHIỆT" } },
            { "tbl.power", new[] { "POWER", "CÔNG SUẤT" } },
            { "session.note", new[] { "FPS (1%) is the average FPS with the 1% low in brackets. Keep the same in-game scene for every mode. Power can only be measured on battery.",
                                      "FPS (1%): FPS trung bình, trong ngoặc là 1% low. Giữ cảnh trong game giống nhau giữa các chế độ. Công suất chỉ đo được khi chạy pin." } },
            { "footer.admin", new[] { "ADMIN", "ADMIN" } },
            { "footer.noadmin", new[] { "NOT ADMIN: TEMPERATURE UNAVAILABLE", "KHÔNG CÓ QUYỀN ADMIN: KHÔNG ĐỌC ĐƯỢC NHIỆT ĐỘ" } },
        };

        public static string S(string key)
        {
            string[] v;
            return Map.TryGetValue(key, out v) ? v[Vi ? 1 : 0] : key;
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(S(key), args);
        }

        public static string ModeName(ThermalMode m)
        {
            return m == ThermalMode.Unknown ? "—" : S("mode." + (int)m);
        }
    }

    /// <summary>Tiny key=value settings file in %APPDATA%\LenoMode.</summary>
    static class Settings
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LenoMode");
        static readonly string FilePath = Path.Combine(Dir, "settings.ini");
        public static int TestSeconds = 60;
        public static bool OverlayOn, TestLoad;
        public static int OverlayCorner, OverlayFields = Overlay.DefaultFields, OverlaySize = 1, OverlayOpacity = 2;
        public static bool OverlayBar;
        public static double OverlayX = -1, OverlayY = -1;   // dragged position (fraction of the work area); -1 = corner

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "lang") L.Vi = v != "en";
                    else if (k == "testSeconds") int.TryParse(v, out TestSeconds);
                    else if (k == "overlay") OverlayOn = v == "1";
                    else if (k == "testLoad") TestLoad = v == "1";
                    else if (k == "overlayCorner") int.TryParse(v, out OverlayCorner);
                    else if (k == "overlayFields") int.TryParse(v, out OverlayFields);
                    else if (k == "overlaySize") int.TryParse(v, out OverlaySize);
                    else if (k == "overlayOpacity") int.TryParse(v, out OverlayOpacity);
                    else if (k == "overlayBar") OverlayBar = v == "1";
                    else if (k == "overlayX") double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out OverlayX);
                    else if (k == "overlayY") double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out OverlayY);
                }
                if (TestSeconds < 10) TestSeconds = 60;
                if ((OverlayFields & ((1 << Overlay.FieldCount) - 1)) == 0) OverlayFields = Overlay.DefaultFields;
                OverlaySize = Math.Max(0, Math.Min(2, OverlaySize));
                OverlayOpacity = Math.Max(0, Math.Min(2, OverlayOpacity));
            }
            catch (Exception) { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] { "lang=" + (L.Vi ? "vi" : "en"), "testSeconds=" + TestSeconds,
                    "overlay=" + (OverlayOn ? "1" : "0"), "testLoad=" + (TestLoad ? "1" : "0"), "overlayCorner=" + (OverlayCorner & 3),
                    "overlayFields=" + OverlayFields, "overlaySize=" + OverlaySize, "overlayOpacity=" + OverlayOpacity,
                    "overlayBar=" + (OverlayBar ? "1" : "0"),
                    "overlayX=" + OverlayX.ToString("0.####", CultureInfo.InvariantCulture),
                    "overlayY=" + OverlayY.ToString("0.####", CultureInfo.InvariantCulture) });
            }
            catch (Exception) { }
        }
    }
}
