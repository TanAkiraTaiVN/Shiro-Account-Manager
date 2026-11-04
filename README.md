# Shiro Account Manager

![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue)
![Framework](https://img.shields.io/badge/.NET-10.0-purple)
![Architecture](https://img.shields.io/badge/UI-Avalonia%2011-indigo)
![License](https://img.shields.io/badge/License-GPL--3.0-green)

**Shiro Account Manager** là trình quản lý đa tài khoản Roblox thế hệ mới chạy trên nền tảng **.NET 10 (Windows x64)** với engine đồ họa tiên tiến **Avalonia UI 11 (Google Skia)** với giao diện Fluent Dark Theme, tối ưu hóa hiệu năng cao, mở ứng dụng tức thì, quản lý hàng đợi mở game thông minh và bảo mật dữ liệu an toàn.

---

## 🌟 Tính năng nổi bật

- **Quản lý đa tài khoản không giới hạn**: Lưu trữ danh sách tài khoản theo nhóm (Groups), tìm kiếm nhanh, ghim tài khoản yêu thích, tự động tính toán tổng số dư Robux.
- **Multi-Roblox Client**: Hỗ trợ mở đồng thời nhiều tài khoản Roblox trên cùng một máy tính với cơ chế xếp hàng tuần tự (`LaunchCoordinator`), tránh lỗi đè process và tránh xung đột singleton mutex.
- **Đăng nhập nhanh trong ứng dụng (In-App Fast Login)**: Tích hợp Microsoft Edge WebView2 nhúng, tự động điền tài khoản, tự động bắt cookie `.ROBLOSECURITY` và tự triệt tiêu banner Cookie Consent.
- **Bảo mật tuyệt đối**: Dữ liệu tài khoản được mã hóa bằng AES-256-GCM + PBKDF2-SHA256 (210.000 vòng lặp), khóa Vault mặc định được bảo vệ bằng Windows DPAPI `CurrentUser` (chế độ Portable giữ khóa thô để di chuyển), hỗ trợ mật khẩu tùy chọn, ghi đĩa nguyên tử (`atomic replacement`) kèm bản sao lưu an toàn (`.backup`).
- **Trình theo dõi Roblox Watcher & Dọn dẹp RAM**:
  - Tự động phát hiện lỗi mất kết nối, lỗi mã 429 và thông báo rate limit.
  - Tối ưu bộ nhớ (`MemoryOptimizer`) tự động giải phóng Working Set của Shiro xuống ~17 MB.
  - Tự động dọn sạch biểu tượng khay hệ thống (Ghost Tray Icons) khi game thoát.
- **Server List & Khám phá Server**:
  - Tra cứu danh sách server theo Place ID, hiển thị độ trễ ping và vị trí địa lý của server (Server Region).
  - Hỗ trợ vào VIP Server, tìm kiếm server nhỏ, tự động đổi Job ID ngẫu nhiên (`Shuffle Job ID`).
- **Điều khiển trong game (Nexus Controller)**: Tích hợp WebSocket Server nội bộ hỗ trợ kết nối và điều khiển tự động các tài khoản đang chơi trong game.
- **Chống treo máy (Anti-AFK)**: Tự động gửi phím ảo định kỳ bảo vệ tài khoản không bị ngắt kết nối do không hoạt động (idle 20 phút).

---

## 📥 Tải về & Cài đặt

Ứng dụng được đóng gói thành file thực thi duy nhất **Single-File Executable**, tích hợp sẵn .NET 10 Runtime không yêu cầu cài đặt thêm thư viện phụ thuộc:

1. Tải bản phát hành mới nhất: file `Shiro.exe` trong thư mục `build\Release\`.
2. Tạo một thư mục riêng (ví dụ: `C:\ShiroRAM\`) và đặt file `Shiro.exe` vào đó.
3. Chạy file `Shiro.exe` (hoặc chạy qua script hiện đại `powershell -File .\start.ps1`) để bắt đầu sử dụng.

> [!NOTE]
>
> - Yêu cầu hệ điều hành: **Windows 10 / 11 (64-bit)**.
> - Dữ liệu ứng dụng được lưu tại `%LocalAppData%\Shiro` để đảm bảo không bị thất lạc khi cập nhật file `.exe`. Nếu muốn chạy dạng Portable (lưu data cạnh file EXE), chỉ cần tạo một file trống tên `portable.mode` cùng thư mục với file `.exe`.

---

## 🛠️ Hướng dẫn Build từ mã nguồn

Dự án được xây dựng trên **.NET 10 SDK** (x64):

```powershell
# 1. Khôi phục packages ở chế độ locked
dotnet restore "Shiro.sln" --runtime win-x64 --locked-mode

# 2. Chạy script đóng gói tự động thành single-file
powershell -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

Sau khi hoàn tất, file thực thi phát hành chuẩn sẽ được tạo tại:
`build\Release\Shiro.exe`

---

## 📋 Kiểm thử tự động & Coverage Gates (Unit Tests)

Dự án sở hữu bộ test suite tự động với hơn 1,400 unit tests bao phủ toàn diện tính toàn vẹn của mã hóa AES-256-GCM / PBKDF2-SHA256, bảo mật DPAPI, quản lý hàng đợi mở game, kiến trúc MVVM strongly-typed, engine đồ họa Avalonia UI 11 và cấu hình JSON.

Các cổng chất lượng kiểm thử (Coverage Gates) được kiểm tra tự động trong CI/CD pipeline và `ArchitectureTests`:
- **Overall Line Coverage**: **65.37%** (Ngưỡng yêu cầu: >= 65.0%)
- **Overall Branch Coverage**: **51.53%** (Ngưỡng yêu cầu: >= 50.0%)
- **Core Security/Persistence Coverage**: **90.39%** (Ngưỡng yêu cầu: >= 90.0%)

Chạy toàn bộ unit test suite:
```powershell
dotnet test tests\Shiro.Tests
```

Kiểm tra ngưỡng coverage với Cobertura:
```powershell
powershell -File .\scripts\Check-Coverage.ps1 -CoverageDir "coverage" -MinOverall 65.0 -MinBranch 50.0 -MinCore 90.0
```

---

## ⚠️ Lưu ý Sử dụng & Miễn trừ Trách nhiệm (Disclaimer)

- Một số tính năng tự động hóa tùy chọn (như **Multi-Roblox**, **Anti-AFK**, hoặc **AltGen**) có thể không tương thích với Điều khoản Dịch vụ (Terms of Service) của Roblox. Các tính năng này mặc định được kiểm soát qua hộp thoại xác nhận rủi ro (`RiskDisclaimerService`). Người dùng tự chịu hoàn toàn trách nhiệm khi kích hoạt và sử dụng các tính năng này trên tài khoản của mình.
- **Shiro Account Manager** không liên kết, tài trợ hay được ủy quyền bởi Roblox Corporation.

---

## ⚖️ Bản quyền & Giấy phép (GPL-3.0 Attribution)

- **Shiro Account Manager** là bản fork được tái cấu trúc và hiện đại hóa toàn diện sang **.NET 10 / C# 13 / Avalonia UI 11** dựa trên dự án mã nguồn mở gốc [Roblox Account Manager (RBX Alt Manager)](https://github.com/ic3w0lf22/Roblox-Account-Manager) của tác giả **ic3w0lf22**.
- Phát hành theo giấy phép mã nguồn mở **GNU General Public License v3.0 (GPL-3.0)**. Xem chi tiết tại [LICENSE](LICENSE) và [NOTICE.md](NOTICE.md).
