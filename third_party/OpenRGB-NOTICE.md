# OpenRGB（隨 Pulse 附帶的第三方程式）

這個資料夾裡的 OpenRGB 是獨立的開源程式，**不是 Pulse 的一部分**：

- 專案：OpenRGB — Open source RGB lighting control that doesn't depend on manufacturer software
- 作者：Adam Honse（CalcProgrammer1）與 OpenRGB 貢獻者
- 授權：GNU General Public License v2.0（全文見同資料夾的 `LICENSE-GPL-2.0.txt`）
- 原始碼：<https://gitlab.com/CalcProgrammer1/OpenRGB>（各版本原始碼與發行檔：<https://openrgb.org/releases.html>）
- 這份副本的來源與檔案雜湊：見同資料夾的 `SOURCE.txt`

Pulse（MIT 授權）只把 OpenRGB 當成另一個程式來啟動（`OpenRGB.exe --server --server-port 6742 --noautoconnect`），
並透過 OpenRGB SDK 的 TCP 協定與它溝通；兩者沒有連結（link）在一起，也沒有修改 OpenRGB 的任何檔案。

依 GPL-2.0 第 3 條，若您取得的是 OpenRGB 的執行檔，可從上方連結取得對應版本的完整原始碼。
