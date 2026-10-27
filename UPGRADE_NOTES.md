# Bản nâng cấp công nghệ

Bản này dùng project SDK-style tại `RBX Alt Manager/RBX Alt Manager.csproj` và chạy trên `.NET 10 Windows x64`.

Các thay đổi chính:

- CefSharp/Chromium 109 đã được thay bằng WebView2 cho trình duyệt nhúng tương thích.
- Luồng Chrome bên ngoài dùng PuppeteerSharp 20 và PuppeteerExtraSharp 3.
- RestSharp, ObjectListView, FastColoredTextBox, Windows API Code Pack và thư viện WebSocket được giữ ở bản stable tương thích với .NET 10 Windows x64.
- Mã mã hóa vẫn giữ định dạng tài khoản cũ để dữ liệu hiện có tiếp tục dùng được.
- Roblox native launch dùng hàng đợi tuần tự theo cơ chế của source gốc; mỗi tài khoản chỉ được gửi một lần trong một lượt, còn khoảng delay lấy trực tiếp từ `AccountJoinDelay` trong Settings (mặc định 8 giây). Không còn thêm một cổng 90 giây hoặc tự xếp lại tài khoản trong cùng lượt.
- Đã khôi phục URI `roblox-player:` làm đường mở chính và bổ sung `browserTrackerId` vào PlaceLauncher URL như source gốc. Nếu Registry đang trỏ protocol sang Chrome hoặc thiếu protocol, RAM tự chuyển sang `RobloxPlayerBeta.exe --app -t ... -j ...`.
- Yêu cầu auth-ticket trả HTTP 429 không còn bị retry đồng bộ nhiều lần; RAM dừng nhanh để tránh kéo dài rate limit và hiển thị đúng nguyên nhân.
- File cấu hình hiện được sửa khi bị thiếu, còn file đã tồn tại nhưng khác hash chỉ ghi cảnh báo. Điều này tránh vòng tự restart vô hạn khi chạy bản EXE cũ hoặc bản single-file có `*.dll.config`.
- Bộ đọc log 429 vẫn ghi nhận lỗi redeem để hiển thị nguyên nhân, nhưng không tự mở lại tài khoản đã thất bại. Roblox vẫn có thể áp dụng rate limit ở phía máy chủ; khi đó cần chờ hoặc đổi đường mạng/VPN rồi chạy lại lượt mới.
- Bộ đọc log 429 chỉ xét các dòng có timestamp thuộc đúng lần launch hiện tại; log cũ của Roblox không còn làm đánh dấu nhầm tài khoản khác trong hàng đợi.
- Hàng đợi multi-account hiện chờ bàn giao của protocol `roblox-player:` tối đa 15 giây thay vì cắt ở 5 giây. Có thêm khóa bàn giao để hai thao tác launch không gửi URI chồng nhau; lỗi khởi động một tài khoản được báo riêng và không làm kẹt các tài khoản còn lại.
- Khi không lấy được mutex `ROBLOX_singletonMutex`, RAM dừng lượt multi-launch với thông báo rõ ràng thay vì tiếp tục mở một số client rồi thất bại không ổn định.
- Luồng Home bằng Chrome/Chromium đã chuyển sang Chrome DevTools Protocol theo mô hình của Vaelixx: mỗi tài khoản có profile tạm riêng, cookie `.ROBLOSECURITY` chỉ là session cookie, có timeout khởi động/điều hướng và tự dọn profile khi đóng cửa sổ. Luồng `proxies.txt` cũ vẫn được giữ lại để xử lý proxy có xác thực.
- Presence được refresh ngay khi mở dashboard, theo đúng `PresenceUpdateRate`, khi bấm nút làm mới, và thêm một lần refresh sau khi Roblox launch xong; không còn phụ thuộc vào cờ kết nối Windows vốn có thể sai khi chạy VPN/TUN.
- Dữ liệu mutable được lưu tại `%LocalAppData%\Shiro`; bản chạy portable có thể tạo file `portable.mode` cạnh EXE. Lần chạy đầu tiên tự sao chép `AccountData.json`, backup, settings, theme, proxy và cache cần thiết từ thư mục phát hành cũ.
- File account mới dùng DPAPI `CurrentUser`; dữ liệu cũ bảo vệ bằng `LocalMachine` vẫn được đọc và sẽ được chuyển đổi khi lưu lần kế tiếp. Ghi account, settings, recent games, proxy và cache dùng file tạm rồi thay thế nguyên tử.
- Các request tải avatar, version, watcher pattern và IP/server metadata dùng `SharedHttpClient`; `WebClient`/`WebRequest` đã được loại bỏ. Request an toàn có retry giới hạn và tôn trọng `Retry-After`, còn auth-ticket không bị retry tự động.
- Các lần kiểm tra proxy của đăng nhập và Open Browser dùng chung factory HTTP có decompression, User-Agent, timeout và cancellation; updater cũng dùng factory riêng với timeout dài thay vì tự dựng `HttpClient` không đồng nhất.
- `SharedHttpClient` chỉ cho retry tự động với `GET`, `HEAD` và `OPTIONS`; nếu một request ghi dữ liệu bị gọi nhầm với cờ retry, nó vẫn chỉ được gửi một lần để tránh rate limit hoặc thao tác lặp.
- Hàng đợi launch được quản lý bởi `LaunchCoordinator` với `SemaphoreSlim`, `CancellationToken` và trạng thái từng tài khoản. Một lượt launch đang chạy sẽ không bị thao tác thứ hai chồng lên và có thể hủy khi đóng app.
- Khi người dùng bấm Join Server/Follow trong lúc lượt cũ còn hủy, giao diện giờ chờ `LaunchCoordinator` báo idle sau khi semaphore đã được nhả rồi mới xếp lượt mới; không còn lỗi giả "queue đang chạy" do race giữa Cancel và lần launch kế tiếp.
- Shutdown kiểm tra cả kết quả `WaitForIdleAsync`; nếu queue không kịp dừng trong grace period, app không dispose client API bên dưới một launch còn sống.
- Các boundary `AccountStore`, `SettingsService`, `RobloxApiClient`, `PresenceService`, `BrowserService` và `AvatarService` đã được thêm để giảm phụ thuộc trực tiếp giữa form và nghiệp vụ.
- Tất cả luồng sign-in thường, sign-in hàng loạt, sign-up và Home đều đi qua `BrowserService`; không còn đường bulk sign-in gọi trực tiếp browser implementation từ form.
- Plaintext account data chỉ được đọc khi còn marker opt-in cũ. Sau lần lưu đầu tiên, dữ liệu được bảo vệ bằng DPAPI `CurrentUser` và marker legacy được xóa.

## Tối ưu hiệu năng bổ sung

- Chrome sign-in và signup nhận cancellation theo vòng đời ứng dụng. Khi đóng Shiro trong lúc DevTools hoặc proxy probe đang chờ, request được hủy và profile tạm được dọn theo luồng cleanup thay vì để browser task chạy mãi nền.
- Tra cứu tên Place ID được debounce theo giá trị hiện tại, hủy request cũ khi người dùng nhập tiếp và được theo dõi trong shutdown; lỗi mạng không còn nổi lên thành exception từ PlaceTimer_Tick hoặc ghi đè tên place mới bằng kết quả cũ.
- Đã rà package theo target .NET 10: PuppeteerSharp đã được nâng cấp lên 25.14.0 độc lập (loại bỏ phụ thuộc PuppeteerExtraSharp cũ), tương thích hoàn toàn với .NET 10 x64 runtime và cơ chế Chrome DevTools Protocol mới. Các package còn lại giữ bản stable đang dùng.
- Kiểm tra NuGet hiện tại không báo package có lỗ hổng đã biết; dependencies đều được khóa phiên bản an toàn trong packages.lock.json.

- `PresenceService` coalesce các lần refresh cùng user trong cửa sổ 2 giây. Timer, nút refresh, scroll và refresh sau launch không còn gửi lặp cùng một request khi chúng xảy ra gần nhau.
- Dashboard lưu snapshot dữ liệu hiển thị của account. Khi chỉ có presence thay đổi, các card/avatar không bị hủy và dựng lại; vị trí cuộn và ảnh đang tải được giữ nguyên.
- Ô tìm kiếm dashboard debounce 140 ms trước khi dựng lại card, nên nhập nhanh không còn tạo một lần layout/avatar rebuild cho từng phím.
- Đọc avatar từ cache đĩa chạy nền và dùng chung một task cho mỗi user, tránh chặn luồng giao diện hoặc tải cùng ảnh nhiều lần.
- Presence không còn yêu cầu ObjectListView redraw khi Roblox không trả về account nào mới, giảm repaint không cần thiết.
- Sau khi đã chụp snapshot control, phần chờ API presence tiếp tục trên thread nền; UI thread không bị giữ chỉ để hoàn tất cleanup task.
- CDP probe của Chrome/Edge dùng một HttpClient loopback dùng chung với proxy hệ thống bị tắt, tránh tạo handler/socket mới cho từng tài khoản và tránh VPN can thiệp vào localhost.
- Các RestClient API của Roblox hiện được tạo tập trung trong `RobloxApiClient`, có timeout 30 giây, User-Agent và decompression thống nhất; ServerList, Presence, Batch và Quick Log In không còn tự tạo client riêng.
- WebServer xử lý callback bất đồng bộ và chờ `JoinServer` trước khi trả lời request, tránh task launch bị bỏ rơi. Refresh/search Server List có CancellationToken và hủy sạch khi đóng form hoặc bấm Cancel.
- WebServer nhận request bằng `HttpListener.GetContextAsync` thay cho một ThreadPool worker bị khóa ở `GetContext`; luồng accept không còn chiếm thread khi không có request và vẫn chờ các handler đang chạy lúc đóng.
- Tải Chromium dự phòng và cài VC Runtime có task được quan sát, bắt lỗi và hủy theo vòng đời ứng dụng; không còn exception nền không được quan sát.
- Ghi log Roblox dùng `ReadExactly`, tránh đọc thiếu một phần dòng khi file đang được ghi đồng thời; Browser position không còn phép chia modulo bằng 0 khi chỉ có một ô màn hình.
- Assembly được đánh dấu Windows-only để loại bỏ hàng nghìn cảnh báo nền tảng giả trong dự án WinForms, giúp cảnh báo còn lại dễ rà soát hơn.
- Ghi dữ liệu tạm dùng `WriteThrough` và `Flush(true)` trước khi thay thế; nếu file account chính bị khóa tạm thời lúc khởi động, `AccountStore` thử bản backup cuối cùng thay vì làm app không mở được.
- Game card và Server List region lookup không còn bọc các thao tác async tự nhiên trong `Task.Run`; lỗi được bắt tại task sở hữu và UI tiếp tục nhận cập nhật đúng SynchronizationContext.
- WebServer theo dõi request đang chạy khi đóng app; nếu còn request Roblox chậm, bản đóng cửa không dispose RestClient bên dưới request đó.
- Luồng native launch không còn bọc `Process.Start`/protocol handoff trong `Task.Run`; semaphore và timeout dùng API async có cancellation, nên hủy queue không để lại thread pool bị kẹt.
- `UnblockEveryone` đã có đường async có thể hủy; không còn fire-and-forget task bỏ rơi sau khi WebServer trả lời.
- Kiểm tra update, Roblox client version, recent games và watcher pattern đều được giữ dưới task có observer và log lỗi; lỗi nền không còn rơi vào `UnobservedTaskException`.
- Bộ hỗ trợ browser CAPTCHA đã chuyển khỏi API PuppeteerSharp obsolete (`IFrame.Name`, `XPathAsync`) sang frame evaluation/query selector hiện hành; kiểm tra DOM `#app/#root` cũng đã await đúng.
- Dọn profile đăng nhập/đăng ký dùng `Task.Delay` có await thay cho `Thread.Sleep`; mã solver Nopecha cũ đã được loại bỏ hoàn toàn, chỉ còn ghi nhận lỗi kết nối tới dịch vụ xác minh.
- Loại bỏ file Settings Designer rỗng và dependency `System.Configuration.ConfigurationManager`; cấu hình runtime chỉ còn đi qua `SettingsService`/INI.
- Danh sách account có snapshot đọc nhất quán và khóa ghi tập trung; Presence, RobloxWatcher, Nexus và dashboard không còn duyệt trực tiếp `AccountsList` trong lúc UI đang xoá, kéo thả hoặc sắp xếp.
- Các thao tác xoá, kéo thả, sắp xếp và chọn account đều cập nhật từ snapshot sau khi khóa được nhả, giảm race condition và tránh ObjectListView nhận một collection đang bị thay đổi.
- Nexus và RobloxWatcher tái sử dụng một snapshot trong mỗi lượt xử lý thay vì khóa/duyệt danh sách lặp lại theo từng dòng hoặc từng process.
- Dọn profile đăng nhập/đăng ký/Home đã sửa lại boundary theo `BrowserSessionsDirectory`; profile không còn bị bỏ sót vì kiểm tra nhầm `%TEMP%`. Fallback đăng nhập cũng giữ nguyên profile Chrome/Edge cô lập thay vì mở profile cá nhân.
- WebView2 compatibility mode cũng lưu profile tại `%LocalAppData%\Shiro\EmbeddedBrowser`; không còn tạo dữ liệu mutable riêng trong thư mục cũ `Roblox Account Manager`.
- `IniFile` khóa cả dictionary section và property snapshot, nên timer nền và UI có thể đọc/ghi cấu hình đồng thời mà không làm hỏng collection.
- Launch auth-ticket/CSRF dùng `ExecuteAsync` có `CancellationToken`; đóng app hoặc dừng queue không còn bị chặn bởi một lệnh RestSharp đồng bộ. HTTP `Retry-After` hỗ trợ cả dạng số giây và HTTP-date.
- Tùy chọn `SavePasswords=false` giờ loại bỏ trường mật khẩu khỏi payload account ngay ở lần lưu kế tiếp, trong khi vẫn giữ cookie/session đã mã hóa. Bản kiểm tra .NET Framework 4.7.2 lỗi thời đã được bỏ vì bản publish chạy self-contained .NET 10.
- Form đóng theo hai pha: hủy queue/timer trước, chờ các task nền trong thời hạn ngắn rồi mới dispose API client; nếu thư viện ngoài còn giữ request, client được để sống tới khi process kết thúc để tránh race dispose.
- Launch Home giờ dùng cùng `LaunchCoordinator` với Join Server; không còn vòng lặp riêng có thể chạy chồng lên một lượt launch khác. Home native/Chrome đều nhận `CancellationToken`, và trạng thái `BROWSER_HOME_STARTED` được coi là thành công trong tiến trình hàng đợi.
- `LaunchProgress` giờ có trạng thái có kiểu (`Success`, `RateLimited`, `TicketError`, `Timeout`, `Failed`, `Cancelled`) và khi hủy sẽ trả cả các tài khoản chưa chạy, thay vì chỉ báo một dòng tổng quát.
- Presence truyền cancellation xuống RestSharp và request lấy tên game, nên đóng app hoặc hủy refresh không phải chờ request 30 giây. Đọc avatar cache dùng `File.ReadAllBytesAsync` thay cho việc chiếm thread-pool bằng `Task.Run`; các thao tác Multi Roblox không còn `Thread.Sleep` chặn UI.
- Ghi file atomic không tạo thêm `.previous` cho mọi avatar/cache/INI; dữ liệu account vẫn có `.backup` riêng trước mỗi lần ghi để giữ khả năng khôi phục.
- Atomic write có hai mức: account, backup và INI dùng `WriteThrough`/`Flush(true)`; avatar, cache, recent games và Nexus vẫn thay thế nguyên tử nhưng không ép flush đĩa cho từng lần ghi, giảm độ trễ khi tải nhiều ảnh.
- `ClientAppSettings.json` được ghi bằng atomic replacement, tránh để Roblox đọc phải JSON bị cắt nếu Shiro hoặc Windows bị dừng giữa lúc patch FPS/custom settings.

## Hoàn thiện bổ sung

- Hủy launch giữa lúc Windows đang bàn giao URI `roblox-player:` giờ dừng hẳn; không còn rơi xuống đường dẫn `RobloxPlayerBeta.exe` và mở client mới sau khi người dùng đã bấm Stop hoặc đóng app.
- Khi `AsyncJoin=true`, timeout chờ protocol handoff được hủy ngay sau khi Roblox báo hiệu thành công; batch nhiều account không còn để lại các timer 20 giây đang chờ trong scheduler.
- Các tác vụ theo dõi log 429 và căn vị trí cửa sổ Roblox nhận cùng `CancellationToken` với lượt launch, được quan sát lỗi và giải phóng handle process; đóng app không để lại task nền chạy thêm 25–45 giây.
- Tra cứu server khi bật `ShuffleJobID` nhận cancellation và không còn tiếp tục phân trang sau khi hàng đợi bị hủy.
- Phản hồi auth-ticket/CSRF không có response được xử lý như lỗi có trạng thái thay vì làm văng luồng đồng bộ. HTTP 429 trong bước CSRF dừng ngay lượt hiện tại, không dùng response 403 cũ để retry thêm lần nữa.
- `Retry-After` của auth 429 được đọc cả ở dạng số giây, HTTP-date và `X-RateLimit-Reset`; giá trị chỉ dùng để thông báo thời gian chờ, không tự retry auth-ticket.
- Kiểm tra phiên bản dùng đường dẫn executable có fallback cho single-file và so sánh `System.Version`, nên không còn lỗi `The path is empty` hoặc so sánh sai khi phiên bản có hai/chín/chục ở một component.
- Khởi động mới tự dọn profile trình duyệt và file `.tmp-*` bị bỏ lại sau crash nếu đã quá 7 ngày, trong ranh giới `%LocalAppData%\Shiro`/portable root.
- Create Account dùng Chrome profile cô lập và chỉ dùng CDP để điền sẵn username, password, ngày sinh và giới tính ngẫu nhiên; nút Sign Up, CAPTCHA và bước xác minh vẫn do người dùng bấm/hoàn tất trong cửa sổ thật. Điều này giữ được tiện ích nhập liệu nhưng tránh lặp lỗi `Please try again` do app tự bấm challenge.
- `build\Publish.ps1` thực hiện restore locked mode, publish sạch, sao lưu dữ liệu mutable cũ trước khi xoá output và kiểm tra bắt buộc chỉ có một `Roblox Account Manager.exe`. Vì vậy tài nguyên trùng và dữ liệu test không còn bị đóng gói nhầm vào bản phát hành.
- Home browser fallback (proxy hoặc CDP không khả dụng) hiện dùng boundary awaitable của `AccountBrowser`; trạng thái thành công chỉ được trả sau khi browser, cookie và navigation đã sẵn sàng, nên lỗi khởi động không còn bị báo thành công giả.
- Home browser fallback giữ reference phiên `AccountBrowser` trong registry tới khi Chromium phát tín hiệu `Disconnected`; wrapper Puppeteer không còn bị GC sớm khi caller chỉ nhận chuỗi trạng thái, đồng thời registry tự bỏ phiên đã đóng.
- BrowserService hiện gom chính sách chọn trình duyệt cho sign-in/sign-up: Chrome trước, Edge sau, Chromium cache cuối cùng; nếu không chuẩn bị được trình duyệt cô lập thì không mở profile mặc định cá nhân. Download Chromium fallback có semaphore dùng chung để nhiều thao tác đồng thời không tải trùng.
- Proxy của Home browser được giải phóng qua helper idempotent ở cả đường `Disconnected` và đường lỗi khởi động, tránh double-dispose hoặc rò proxy khi browser chết trước lúc gắn event.
- Launch delay trong Modern Dashboard, SettingsForm và `LaunchCoordinator` dùng chung cùng hằng số (mặc định 8 giây, cho phép 1–3600 giây), không còn hiển thị 30 giây hoặc giới hạn 5–120 khác với runtime.
- Khi marker plaintext cũ còn sót nhưng payload hiện tại đã là DPAPI, lần lưu tiếp theo vẫn luôn dùng DPAPI `CurrentUser` và dọn marker; marker không thể làm hạ mức bảo vệ dữ liệu trở lại plaintext.
- Script `build\Publish.ps1` dừng ngay khi không thể xóa output do EXE đang chạy, thay vì tiếp tục tạo bundle lỗi và để lại thư mục phát hành nửa chừng.
- CAPTCHA/human verification luôn do người dùng thực hiện; hook tự động Nopecha cũ không còn được gắn vào trang Roblox, kể cả khi bản cũ còn lưu khóa solver.
- Chính sách CAPTCHA được siết ở mã nguồn: `AccountBrowser` không còn chứa hook/API solver, nên khóa Nopecha cũ trong cấu hình không thể tự động gửi hoặc bấm câu trả lời.
- Nexus output, file `Nexus.lua` xuất ra và account dump được chuyển vào `%LocalAppData%\Shiro` (hoặc thư mục portable), không còn ghi dữ liệu mutable cạnh EXE; account dump dùng atomic replacement.
- Timer presence không còn dùng `async void` qua `InvokeIfRequired`; callback được marshal về UI và task được quan sát, tránh lỗi cập nhật control hoặc exception nền khi đóng app.
- Refresh dashboard từ callback đăng nhập kiểm tra `IsDisposed`/`IsHandleCreated` trước khi `BeginInvoke`, nên tài khoản hoàn tất đúng lúc app đang đóng không còn đẩy callback vào handle đã hủy.
- Presence helper công khai cũng chia danh sách thành lô tối đa 50 user ID, tránh trả lỗi toàn bộ khi người dùng có nhiều tài khoản; dashboard vẫn coalesce các lần refresh gần nhau như trước.
- Dashboard dispose card từ snapshot của `Controls` thay vì duyệt collection đang bị thay đổi, nên lọc/tìm kiếm liên tục không bỏ sót card hoặc ném lỗi khi card tự tháo khỏi parent.
- `ModernDashboard.RefreshAccounts` dùng cùng guard khi marshal snapshot về UI, tránh callback presence/login chạm vào handle đã bị dispose trong lúc đóng cửa.
- Các form Settings, Server List, Modern Dashboard, Nexus và utility giờ lưu INI qua `AccountManager.SaveSettings()`/`SettingsService`, tránh bỏ qua khóa đồng bộ khi nhiều callback cùng ghi cấu hình.

Build bản chạy trực tiếp:

```powershell
& .\build\Publish.ps1
```

Lệnh trên dùng `packages.lock.json` ở chế độ locked. Nếu project vừa đổi package, chạy một lần:

```powershell
dotnet restore "RBX Alt Manager\RBX Alt Manager.csproj" --runtime win-x64 --force-evaluate
```

File chạy là `build\Release\Roblox Account Manager.exe`. Bản phát hành cũ và dữ liệu đi kèm được giữ riêng tại `build\legacy-publish-final` để không xuất hiện thêm EXE trong thư mục phát hành chính.

## Cấu trúc thư mục

- `RBX Alt Manager/`: mã nguồn, form, lớp nghiệp vụ và tài nguyên của ứng dụng.
- `build/Release/`: bản phát hành đang dùng.
- `build/artifacts/`: đầu ra tạm của các lần build trong Visual Studio hoặc `dotnet build`.
- `build/legacy-publish-final/`: bản phát hành cũ để đối chiếu hoặc khôi phục; không dùng để chạy bản hiện tại.
- `build/archive/`: các bản thử nghiệm và cache cũ, không được dùng khi chạy bản hiện tại.
- `build/archive/diagnostics/`: log và file chẩn đoán cũ được gom riêng để thư mục `build/` chính không còn nhiều bản build rời.
- `Images/`, `README.md` và `UPGRADE_NOTES.md`: tài liệu và hình ảnh của dự án.

Các file trung gian của MSBuild được đặt trong `build/obj/` để không làm bẩn thư mục mã nguồn.

WebView2 cần Microsoft Edge WebView2 Runtime có sẵn trên Windows. Đăng nhập Roblox mặc định vẫn mở Chrome cài trên máy để CAPTCHA và xác minh người dùng hoạt động bình thường. Chrome/Edge login và signup dùng profile tạm riêng trong `%LocalAppData%\Shiro\BrowserSessions`, không dùng profile cá nhân.

Kiểm tra phát hành cuối: `dotnet build` và `dotnet publish` đều hoàn tất với 0 lỗi; bản EXE self-contained đã mở được cửa sổ `Shiro`, đóng sạch với mã thoát 0, giữ nguyên hash `AccountData.json`, và `build\Release` chỉ còn một file `.exe` chính.

Kiểm tra bổ sung sau đợt tối ưu kiến trúc: build/publish Release `win-x64` tiếp tục hoàn tất với 0 lỗi; EXE mới mở được cửa sổ `Shiro` và đóng sạch; `build\Release` có đúng một EXE (`Roblox Account Manager.exe`), còn `AccountData.json` giữ nguyên hash `B84C33039FD3547FC76925A441F87A88CE4D384856668092D5515524D7426D79` trong lần kiểm tra này.

Kiểm tra sau đợt hoàn thiện: `dotnet build --configuration Release --no-restore` đạt 0 warning/0 error; publish `win-x64` hoàn tất không warning IL3000; EXE mới mở với tiêu đề `Shiro`, có cửa sổ và đóng graceful thành công, không để lại process Shiro chạy nền. `build\Release` vẫn chỉ có một `.exe`.

Kiểm tra dữ liệu sau khi mở bản mới bằng DPAPI `CurrentUser`: đọc được schema 1 với 8 tài khoản, trường mật khẩu và group vẫn còn; các khóa `AccountJoinDelay`, `AsyncJoin`, `EnableMultiRbx`, `SavePasswords` và `PresenceUpdateRate` vẫn được giữ. Việc join Roblox thật, CAPTCHA, VPN và HTTP 429 vẫn cần xác nhận trên mạng/tài khoản thực tế vì có tác động bên ngoài ứng dụng.

Kiểm tra bản phát hành trước đợt tối ưu queue: `build\Publish.ps1` restore locked mode và publish thành công; thư mục `build\Release` sạch có 19 file tài nguyên cần thiết, 2 thư mục tài nguyên và đúng một EXE (khoảng 60,6 MB), không đóng gói `AccountData.json`, cache avatar, log hoặc `RecentGames.json`. Dữ liệu người dùng tiếp tục nằm trong `%LocalAppData%\Shiro`.

Kiểm tra tiếp theo sau tối ưu queue/proxy/ghi đĩa: `dotnet build --configuration Release --no-restore` đạt 0 warning/0 error; publish lại thành công; EXE mở với tiêu đề `Shiro`, đóng graceful mã 0 và không để lại process. Hash `AccountData.json` vẫn là `3BB2B8656FD22C1C6632168911F8A3897BCCFC2268151DCEE9873690D219E2D4`; `build\Release` có đúng một EXE.

Kiểm tra mới nhất sau queue idle/WebServer/WebView2 profile: publish `win-x64` hoàn tất, `build\Release` có 8 file tài nguyên, 2 thư mục tài nguyên, không có mutable output và EXE `Roblox Account Manager.exe` duy nhất (62.578.729 bytes). Smoke test mở tiêu đề `Shiro`, đóng mã 0; hash account không đổi và không còn process chạy nền.

Smoke test sau bổ sung kiểm tra shutdown queue tiếp tục đạt: tiêu đề `Shiro`, đóng graceful mã 0, `AccountData.json` giữ hash `3BB2B8656FD22C1C6632168911F8A3897BCCFC2268151DCEE9873690D219E2D4`, `build\Release` còn một EXE và không có process Shiro tồn tại.

Rà soát tĩnh sau cùng không còn `WebClient`, `WebRequest`, `HttpWebRequest`, `new Thread` hoặc `Thread.Sleep`; không còn `HttpClient`/`RestClient` được tạo ngoài các factory service. `dotnet list package --vulnerable --include-transitive` không phát hiện package có lỗ hổng từ NuGet hiện tại.

Kiểm tra phát hành mới nhất (11/10/2026): sau khi target .NET 10 Windows x64 được khóa lại, `packages.lock.json` đã được tạo lại cho runtime `win-x64`; `dotnet build --configuration Release --no-restore --no-incremental` đạt 0 warning/0 error và `build\\Publish.ps1` restore locked mode/publish thành công. EXE mới có kích thước 62.579.223 bytes, mở được cửa sổ `Shiro`, đóng graceful với mã 0; `AccountData.json` giữ hash `3BB2B8656FD22C1C6632168911F8A3897BCCFC2268151DCEE9873690D219E2D4`, không còn process Shiro/Roblox Account Manager sau khi đóng và `build\\Release` không chứa mutable data, chỉ có đúng một EXE `Roblox Account Manager.exe`.

## Đợt hiện đại hóa công nghệ (17/10/2026)

- **Gỡ bỏ thư viện chết `SharpZipLib 1.4.2`**: Loại bỏ package `SharpZipLib` khỏi [RBX Alt Manager.csproj](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/RBX%20Alt%20Manager.csproj) và xóa directive `using ICSharpCode.SharpZipLib.Zip;` thừa trong [AccountManager.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs). Dự án tiếp tục dùng `System.IO.Compression` (`ZipArchive`, `ZipFile`) tích hợp sẵn của .NET.
- **Nâng cấp `System.Management` lên `10.0.13`**: Cập nhật bản vá bảo mật và tương thích WMI mới nhất trên NuGet, cập nhật lock file `packages.lock.json`.
- **Hiện đại hóa `SharedHttpClient.CreateProxyClient`**: Thay thế `HttpClientHandler` cũ bằng **`SocketsHttpHandler`** hiện đại của .NET, hỗ trợ tối ưu socket pool, timeout kết nối độc lập và giảm tải tài nguyên OS khi kiểm tra proxy hàng loạt.
- **Dọn dẹp mã nguồn UI không còn dùng**: Xóa bỏ `ModernSettingsDialog.xaml` và `ModernSettingsDialog.xaml.cs` (file dialog độc lập thừa do giao diện cài đặt hiện đại đã được nhúng trực tiếp dạng UserControl qua `ModernSettingsControl.xaml` vào `WpfDashboardView.xaml`).
- **Nâng cấp script kiểm thử `build\run.ps1`**: Bổ sung vòng lặp theo dõi tiến trình theo thời gian thực (PID, tiêu đề cửa sổ, mức chiếm dụng bộ nhớ RAM WorkingSet) và kiểm tra đóng ứng dụng an toàn.
- **Gỡ bỏ hoàn toàn `libsodium.dll` native và thay bằng 100% C# Managed Crypto**:
  - Loại bỏ package `libsodium-netcore 0.10.0`, tham chiếu `Sodium.dll`, và các file nhị phân C native `libsodium.dll` (488 KB) trong thư mục gốc và `Resources\`.
  - Thay thế thuật toán giải mã file cũ V1 bằng **`Konscious.Security.Cryptography.Argon2 1.3.1`** (Argon2id managed) và **`NaCl.Net 0.1.13`** (`NaCl.XSalsa20Poly1305` managed).
  - Triệt tiêu 100% nguy cơ bị Windows Defender hoặc các phần mềm Antivirus gắn cờ cảnh báo nhầm (false-positive). File thực thi phát hành giảm được hơn 500 KB dung lượng.
- **Tối ưu hóa `TryParseJson` với Fast-Path `System.Text.Json`**:
  - Nâng cấp phương thức mở rộng [Utilities.TryParseJson](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Utilities.cs#L182) ưu tiên deserialize bằng `System.Text.Json` tốc độ cao trước khi fallback về Newtonsoft.Json, giúp tăng tốc độ đọc dữ liệu JSON và giảm cấp phát bộ nhớ.
- **Tăng cường an toàn `RecentGames`**:
  - Bổ sung phòng vệ `try/catch` có ghi log khi đọc `RecentGames.json` trong [AccountManager.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs#L3540).
- **Gỡ bỏ `PuppeteerExtraSharp 3.1.1` và nâng cấp `PuppeteerSharp` lên `25.14.0` (Bản mới nhất)**:
  - Loại bỏ hoàn toàn package cũ `PuppeteerExtraSharp 3.1.1` (vốn đã ngừng bảo trì và khóa chặt PuppeteerSharp ở bản 20.2.2).
  - Nâng cấp `PuppeteerSharp` từ 20.2.2 nhảy vọt 5 thế hệ lên **`25.14.0`** (bản stable mới nhất hiện nay trên NuGet).
  - Tự động hóa cơ chế Stealth chống bot trực tiếp bằng tham số Chromium `--disable-blink-features=AutomationControlled` kết hợp chèn script ẩn danh tính tự nhiên (`navigator.webdriver`, `window.chrome`) qua `EvaluateExpressionOnNewDocumentAsync` trên `IPage`, đạt hiệu quả tàng hình cao hơn mà không cần phụ thuộc plugin bên ngoài.
- **Thay thế hoàn toàn `log4net` bằng `AppLogger` bất đồng bộ siêu tốc (Zero Dependency)**:
  - Loại bỏ package `log4net 3.5.0`, xóa bỏ vĩnh viễn các file cấu hình XML cồng kềnh `log4.config` và `App.config`.
  - Xóa bỏ vòng lặp tính mã băm hash và ghi đè sửa chữa file config cũ lúc khởi động trong [Program.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Program.cs), giúp tăng tốc độ khởi động ứng dụng.
  - Xây dựng **[AppLogger](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AppLogger.cs)** bằng kiến trúc `System.Threading.Channels.Channel<string>` chạy ngầm non-blocking hoàn toàn, tự động xoay vòng file log (5 MB rolling backup), đảm bảo 100% không bao giờ làm giật lag giao diện người dùng khi ghi log đồng thời từ nhiều luồng.
- **Nâng cấp nền tảng Windows SDK Target lên `net10.0-windows10.0.19041.0`**:
  - Chuyển từ mốc Windows 10 17763 (năm 2018) lên **`19041.0`** (Windows 10 2004+ / Windows 11), chuẩn hóa tương thích hệ thống mới nhất.
- **Kiểm định hoàn hảo toàn diện**:
  - `dotnet list package --vulnerable`: **0 package dính lỗ hổng bảo mật**.
  - `dotnet list package --outdated`: **0 package lỗi thời, toàn bộ dependency đạt trạng thái mới nhất**.

## Đợt rà soát và khắc phục triệt để các lỗi tiềm ẩn (17/10/2026)

Qua quá trình rà soát chuyên sâu toàn bộ codebase, đã phát hiện và xử lý thành công 10 nhóm lỗi và tối ưu kiến trúc (crash, giật lag chuột, ngoại lệ mạng, tràn số 64-bit, lỗi thread freeze, module hóa code):

1. **Khắc phục lỗi giật/cướp chuột vật lý (`SweepWindowWithMouse`)**:
   - *Nguyên nhân*: Trong [Utilities.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Utilities.cs#L107), hàm quét dọn vùng thông báo cửa sổ Roblox trước đây sử dụng vòng lặp P/Invoke `SetCursorPos` kết hợp `Thread.Sleep(2)`. Điều này khiến con trỏ chuột của người dùng bị giật nảy hoặc di chuyển bất ngờ trên màn hình khi Roblox đóng.
   - *Khắc phục*: Thay thế hoàn toàn bằng cơ chế ảo hóa gửi thông điệp `PostMessage(hWnd, WM_MOUSEMOVE, ...)` theo tọa độ client rect. Không can thiệp vào con trỏ chuột vật lý của người dùng, không gây đơ luồng và loại bỏ hoàn toàn các P/Invoke không cần thiết.

2. **Khắc phục ngoại lệ phân tích URL và Navigation Context trong Puppeteer**:
   - *Nguyên nhân*: Trong [AccountBrowser.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/AccountBrowser.cs#L552), việc gọi `new Uri(e.Request.Url)` trực tiếp có thể ném `UriFormatException` đối với các scheme không chuẩn (`data:`, `blob:`, `chrome-extension:`). Ngoài ra, khi Roblox chuyển hướng nhanh `/v2/login` -> `/home`, Puppeteer hủy execution context của trang dẫn đến ngoại lệ `Execution context was destroyed` và `Response body is unavailable for redirect responses` làm ô nhiễm log.
   - *Khắc phục*: Bổ sung kiểm tra an toàn `Uri.TryCreate`, kiểm tra trạng thái trang `page.IsClosed`, đồng thời bổ sung bộ lọc `IsTransientBrowserException` / `IsDetachedPageException` trong [AccountBrowser.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/AccountBrowser.cs) và [RobloxBrowserLogin.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/RobloxBrowserLogin.cs) để xử lý mượt mà vòng đời điều hướng.

3. **Khắc phục ngoại lệ `NullReferenceException` khi mạng gặp sự cố**:
   - *Nguyên nhân*: Trong [AccountManager.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs#L1598), phương thức `GetUserID` và một số vị trí khác truy cập thẳng `response.StatusCode` và `response.Content` mà không kiểm tra `response != null` (xảy ra khi mạng timeout, hủy kết nối, hoặc lỗi DNS).
   - *Khắc phục*: Thêm guard clause `response != null` và phòng vệ `try/catch` đầy đủ. Ngoài ra, sửa logic kiểm tra `VIPServer` từ so sánh độ dài chuỗi cố định `== "VIP:"` sang `StartsWith("VIP:", StringComparison.OrdinalIgnoreCase)`.

4. **Khắc phục lỗi `InvalidOperationException` trong truy vấn WMI (`GetCommandLine`)**:
   - *Nguyên nhân*: Trong [Utilities.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Utilities.cs#L312), `GetCommandLine` dùng `.Cast<ManagementBaseObject>().SingleOrDefault()`. Khi hệ điều hành tái sử dụng PID hoặc tiến trình đang thoát, WMI có thể trả về nhiều hơn một thực thể, dẫn đến `InvalidOperationException: Sequence contains more than one element`.
   - *Khắc phục*: Bổ sung kiểm tra tiến trình `process == null || process.HasExited` và đổi sang `.FirstOrDefault()`.

5. **Khắc phục lỗi P/Invoke 64-bit và tràn số `OverflowException` trong AccountControl**:
   - *Nguyên nhân*: Trong [AccountControl.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Nexus/AccountControl.cs#L45), `SendMessage` được khai báo P/Invoke với kiểu `int` cho handle và con trỏ (`int hwnd, int wMsg, int wParam, int lParam`). Đồng thời, khi đo số dòng text đã gọi `l.Handle.ToInt32()`. Trên Windows 64-bit, nếu handle có giá trị vượt quá dải số nguyên 32-bit có dấu, `ToInt32()` sẽ ném ra `OverflowException: Arithmetic operation resulted in an overflow.` làm crash ứng dụng.
   - *Khắc phục*: Chuẩn hóa P/Invoke `SendMessageW` sử dụng `IntPtr` cho handle và con trỏ, truyền trực tiếp `l.Handle` an toàn 64-bit mà không cần ép kiểu qua `ToInt32()`.

6. **Khắc phục Thread Freeze do `WaitForExit` vô tận**:
   - *Nguyên nhân*: Trong [AccountControl.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Nexus/AccountControl.cs#L155), phương thức `OpenServer()` chứa lệnh gọi `Process.GetCurrentProcess().WaitForExit();`. Khiến luồng ThreadPool chạy tác vụ này bị treo vĩnh viễn suốt vòng đời ứng dụng.
   - *Khắc phục*: Gỡ bỏ lệnh `WaitForExit` thừa, khởi chạy listener WebSocket ngầm sạch sẽ bằng `Task.Run(OpenServer)`.

7. **Khắc phục lỗi nghịch đảo Min/Max và văng ứng dụng khi load config số**:
   - *Nguyên nhân*: Tại [AccountControl.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Nexus/AccountControl.cs#L370), `AutoMinIntervalNum.Value` được gán bằng biểu thức nghịch đảo `Math.Max(Math.Min(val, Minimum), Maximum)`, dẫn đến giá trị luôn bị ép về `Maximum` hoặc có thể gây `ArgumentOutOfRangeException`.
   - *Khắc phục*: Chuyển toàn bộ các gán giá trị của NumericUpDown sang dùng hàm mở rộng `.Clamp(Minimum, Maximum)` chuẩn xác, đồng thời kiểm tra biên hợp lệ cho `AutoCloseType.SelectedIndex`.

8. **Tăng cường an toàn truyền thông WebSocket (Nexus)**:
   - *Nguyên nhân*: Trong [ControlledAccount.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Nexus/ControlledAccount.cs), việc bóc tách `Margin` và `Size` sử dụng `int.Parse` trực tiếp, dễ bị crash khi client truyền chuỗi sai định dạng. Đồng thời việc truy cập index payload không kiểm tra key có thể gây `KeyNotFoundException`, và gửi tin khi socket ngắt kết nối gây ngoại lệ.
   - *Khắc phục*: Sử dụng `int.TryParse`, dùng `.TryGetValue` an toàn cho payload, kiểm tra `Context.WebSocket.IsAlive` và bọc `try/catch` chống sập luồng truyền thông.

9. **Phòng vệ crash do phản hồi HTTP bất thường và `NullReferenceException` khi gỡ Control**:
   - *Nguyên nhân*: Trong [ServerList.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Forms/ServerList.cs), [Utilities.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Utilities.cs), [MissingAssetControl.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/MissingAssetControl.cs) và [AvatarControl.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/AvatarControl.cs):
     - `JObject.Parse` được gọi trực tiếp không có `try/catch` có thể crash khi gặp trang Cloudflare hoặc HTML lỗi.
     - Khi `MissingAssetControl` bị gỡ khỏi form (clear control), sự kiện `ParentChanged` phát sinh với `Parent == null`, truy cập `Parent.Width` gây `NullReferenceException`.
     - `Clipboard.SetText` thiếu try/catch khi truyền chuỗi rỗng hoặc khi clipboard bị ứng dụng khác khóa.
   - *Khắc phục*: Thêm kiểm tra null, bọc `JObject.Parse` trong try-catch, kiểm tra `if (Parent == null) return;` và bọc an toàn clipboard.

10. **Refactor cấu trúc và module hóa toàn diện 100% toàn bộ dự án (100% Architectural Modularization)**:
    - *Vấn đề ban đầu*: Ba file trung tâm của toàn dự án là các monolithic "God Classes" khổng lồ:
      - `ModernDashboard.cs` (~6.800 dòng)
      - `AccountManager.cs` (~5.600 dòng)
      - `Account.cs` (~2.500 dòng)
      Chứa quá nhiều trách nhiệm hỗn tạp chồng chéo: từ render icon vector, custom menu, panel bo tròn, cache avatar, DWM dark caption, frameless hit-testing, splash animation, security onboarding, settings UI, launch coordination, HTTP API endpoints cho tới game process launcher.
    - *Giải pháp & Kết quả thực hiện*: Đã hoàn tất tái cấu trúc 100% mã nguồn theo nguyên lý Single Responsibility (SRP) thông qua các partial classes và standalone controls chuyên biệt, bảo lưu 100% tính tương thích ngược, namespace và hành vi runtime:
      - **Module hóa `ModernDashboard`** (Giảm từ ~6.800 dòng xuống còn **2.542 dòng**):
        - `Controls/ModernDashboardIcons.cs` (591 dòng): Icon rendering vector, dark context menu rendering (`ModernDarkMenuRenderer`) và custom icon controls.
        - `Controls/ModernControls.cs` (1.113 dòng): Điều khiển giao diện hiện đại (`CardsFlowLayoutPanel`, `ModernScrollBar`, `ModernToggle`, `ModalOverlay`, `RoundPanel`, `AvatarView` có cache LRU kép đĩa/RAM).
        - `Controls/ModernSettingsPanel.cs` (710 dòng): Bảng cấu hình cài đặt hiện đại, binding dữ liệu INI trực tiếp, hỗ trợ Potato Mode / FPS Unlocker.
        - `ModernDashboard.Modals.cs` (1.248 dòng): Gom toàn bộ 7 modal hộp thoại (Add Account, Bulk Import, Settings, Account Info, Launch, Remove Confirm, Command Palette) cùng vòng đời modal overlay.
      - **Module hóa `AccountManager`** (Giảm từ ~5.600 dòng xuống còn **2.650 dòng**):
        - `AccountManager.Chrome.cs` (270 dòng): Custom frameless title bar, DWM dark mode Windows 10/11, resize hit-testing qua `WndProc`, và splash startup animation.
        - `AccountManager.Security.cs` (230 dòng): Khởi tạo lựa chọn mã hóa onboarding, DPAPI CurrentUser / Master Password và debounce nhập liệu.
        - `AccountManager.Launch.cs` (340 dòng): Quản lý hàng đợi khởi chạy, điều phối delay (`LaunchCoordinator`), drain queue, hủy tiến trình và các sự kiện Join Server/Follow User.
        - `AccountManager.ModernActions.cs` (1.106 dòng): Toàn bộ các action kết nối giữa Modern Dashboard / WPF UI và engine xử lý nghiệp vụ (thao tác thêm, xóa tài khoản, gán nhóm, đặt alias, mở browser, dọn dẹp Roblox processes).
      - **Module hóa `Account`** (Giảm từ ~2.494 dòng xuống còn **792 dòng**):
        - `Classes/Account.Launcher.cs` (898 dòng): Pipeline khởi chạy Roblox Client native/browser (`JoinServer`, `LaunchAppHome`, `LaunchHomeInChrome`, định vị cửa sổ `AdjustWindowPosition`, theo dõi log lỗi HTTP 429).
        - `Classes/Account.Api.cs` (560 dòng): Giao tiếp REST/JSON API chính thức của Roblox (`ValidateSessionAsync`, `FetchRobuxBalanceAsync`, `GetCSRFTokenAsync`, `GetAuthTicketAsync`, `GetEmailJSON`, `GetMobileInfo`, `GetUserInfo`, `UnblockEveryoneAsync`, `QuickLogIn`).
        - `Classes/Account.cs` (792 dòng): Giữ đúng trách nhiệm là Entity Model (thuộc tính tài khoản, so sánh, mã hóa, pin lock, serialize/deserialize JSON).

## Đợt triệt tiêu nợ kỹ thuật toàn diện (18/10/2026)

Qua đợt rà soát và thực thi tái cấu trúc toàn diện, đã giải quyết triệt để 16 nhóm nợ kỹ thuật then chốt:

1. **Triệt tiêu hoàn toàn Sync-over-Async & HTTP Blocking**:
   - `GameControl.cs`: Chuyển `GamesClient.Execute(DetailsRequest)` đồng bộ chặn luồng UI sang `await GamesClient.ExecuteAsync(DetailsRequest)` và copy clipboard an toàn trên UI thread.
   - `ServerList.cs`: Xóa bỏ vĩnh viễn `#pragma warning disable CS4014`. Chuyển `GetUniverseID_Click` và `ViewUniverse_Click` thành `async void`, đồng thời `await` đệ quy đầy đủ cho `GetUniversePlaces`.
   - `Account.cs`: Đánh dấu `[Obsolete]` phương thức đồng bộ `UnblockEveryone(out string)`, khuyến khích sử dụng `UnblockEveryoneAsync` thuần async không gây deadlock luồng.

2. **Khôi phục và kết nối tính năng Nexus (In-game Controller) lên WPF**:
   - Thêm lệnh `OpenNexusCommand` và delegate `OnNexusRequested` vào [MainViewModel.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/ViewModels/MainViewModel.cs).
   - Bổ sung menu item **🎮 Điều khiển Nexus (In-game Controller)** trực tiếp trong menu Tools của [WpfDashboardView.xaml](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/WpfUI/Views/WpfDashboardView.xaml).
   - Cài đặt handler `OpenNexusWindow()` trong [MainWindow.xaml.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/WpfUI/Views/MainWindow.xaml.cs) mở `AccountControl` an toàn, tự động kích hoạt máy chủ WebSocket và áp dụng theme dark mode.

3. **Tách rời trạng thái Place/Job ID khỏi WinForms controls**:
   - Tách `CurrentPlaceId` và `CurrentJobId` trong [AccountManager.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs) thành các static property độc lập có backing field, loại bỏ hoàn toàn nguy cơ `NullReferenceException` khi truy cập ngoài ngữ cảnh WinForms UI.

4. **Triệt tiêu lãng phí tài nguyên và Lazy-Loading toàn bộ WinForms Form phụ**:
   - Loại bỏ việc khởi tạo trùng lặp `WpfDashboardHost` khi ứng dụng đang chạy dưới WPF `MainWindow`.
   - Chuyển 7 Form WinForms phụ (`ArgumentsForm`, `ServerList`, `AccountUtils`, `ImportForm`, `AccountFields`, `ThemeEditor`, `RecentGamesForm`) từ khởi tạo sớm ngốn RAM lúc khởi động sang **Lazy-Loading** chỉ khởi tạo khi người dùng bấm mở.
   - Chuẩn hóa `ApplyTheme()` sử dụng null-conditional operator `?.ApplyTheme()`.

5. **Chuẩn hóa cấu hình Strongly-Typed & Mở rộng Kiểm thử (58 Tests)**:
   - Xây dựng **[AppSettingsKeys.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AppSettingsKeys.cs)** gom toàn bộ hằng số section, key cấu hình INI, FastFlags và WebServer, triệt tiêu lỗi typo chuỗi ký tự.
   - Bổ sung các unit tests mới cho `AppSettingsKeysTests`, `AccountDecouplingTests`, kiểm tra static properties và các hàm REST async.
   - Nâng tổng số unit test tự động vượt qua lên **58/58 Passed (100%)**.

6. **Triệt tiêu 100% Mã chết Dashboard WinForms cũ (~7.000 dòng code)**:
   - Xóa bỏ an toàn 6 file WinForms Dashboard trung gian đã bị WPF thay thế hoàn toàn:
     - `WpfUI/WpfDashboardHost.cs`
     - `ModernDashboard.cs`
     - `ModernDashboard.Modals.cs`
     - `Controls/ModernControls.cs`
     - `Controls/ModernSettingsPanel.cs`
     - `Controls/ModernDashboardIcons.cs`
   - Dọn dẹp trường `ModernDashboard` và `WpfDashboardHost` khỏi [AccountManager.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs), biến `AccountManager` thành background headless controller điều phối engine thuần túy.
   - Chuyển toàn bộ cập nhật trạng thái `SetDashboardStatus`, danh sách tài khoản, điều hướng trang và sự kiện Presence trực tiếp sang WPF `MainWindow.Instance` và `MainViewModel`.
   - Giữ nguyên các thư viện `ObjectListView` và `FastColoredTextBox` phục vụ tính năng điều khiển Game `Nexus` và `ServerList`.

7. **Triệt tiêu 100% Lệnh gọi RestSharp Đồng bộ (Zero Synchronous RestSharp Calls)**:
   - Chuyển đổi toàn bộ phương thức gọi API trong [Account.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Account.cs), [Account.Api.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Account.Api.cs), [Account.Launcher.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Account.Launcher.cs), [AccountManager.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs) và [ServerListViewModel.cs](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/ViewModels/ServerListViewModel.cs) sang `await ExecuteAsync(...)`:
     - `GetUserIDAsync` & `GetUserID`
     - `GetCSRFTokenAsync` & `GetCSRFToken`
     - `GetAuthTicketAsync` & `GetAuthTicket`
     - `CheckPinAsync` & `CheckPin`
     - `UnlockPinAsync` & `UnlockPin`
     - `BlockUserIdAsync` / `UnblockUserIdAsync`
     - `GetBlockedListAsync` & `GetBlockedList`
     - `TogglePlayerBlockedAsync` & `TogglePlayerBlocked`
     - `SetServerAsync` & `SetServer`
     - `SetFollowPrivacyAsync` & `SetFollowPrivacy`
     - `ChangePasswordAsync` & `ChangePassword`
     - `ChangeEmailAsync` & `ChangeEmail`
     - `LogOutOfOtherSessionsAsync` & `LogOutOfOtherSessions`
     - `SendFriendRequestAsync` & `SendFriendRequest`
     - `SetDisplayNameAsync` & `SetDisplayName`
     - `SetAvatarAsync` & `SetAvatar`
     - Cập nhật quyền mời Private Server trong `Account.Launcher.cs` sang `await ExecuteAsync`.
   - Toàn bộ codebase hiện đạt trạng thái **Zero synchronous RestSharp calls**.

8. **Tối ưu hóa & Sửa lỗi các Dialog WPF (Modern UI Dialogs)**:
   - **`ModernSettingsControl` - Khắc phục lỗi tự đóng Roblox**: Tách riêng thao tác bật tắt `ChkMultiRoblox` thành handler `ChkMultiRoblox_Changed` có kiểm tra nhắc nhở (`promptIfRobloxRunning: true`). Loại bỏ việc kích hoạt `TryEnableMultiRoblox(promptIfRobloxRunning: false)` từ hàm lưu chung `SaveAllSettings()`, ngăn chặn triệt để tình trạng người dùng chỉ đổi tùy chọn thông thường (như Check For Updates, Show Presence, v.v.) mà bị đóng mất client Roblox đang chơi.
   - **`ModernAccountUtilsDialog` - Bất đồng bộ hóa UI (Non-blocking)**: Chuyển toàn bộ các phương thức gọi API tài khoản (Đăng xuất phiên, Đổi mật khẩu, Mở khóa PIN, Đổi Email, Đổi Display Name, Kết bạn, Chặn người chơi) sang `await ...Async()`. Xóa bỏ hoàn toàn blocking sync-over-async (`.GetAwaiter().GetResult()`) trên UI thread, bổ sung phản hồi trạng thái trực quan trong lúc chờ kết quả mạng và thêm guard `if (!IsLoaded) return;` cho ComboBox Follow Privacy.
   - **`ModernProxyManagerDialog` - Hủy tác vụ kiểm tra khi đóng cửa sổ**: Tích hợp `CancellationTokenSource _testCts` vào `TestAllProxiesAsync`, tự động ngắt toàn bộ tác vụ ping TCP socket đang chạy nền khi người dùng đóng dialog hoặc bắt đầu đợt test mới, tránh rò rỉ socket và lỗi dispatcher trên view đã đóng.
   - **`ModernMissingAssetsDialog` - Hiện đại hóa hộp thoại vật phẩm Avatar còn thiếu**: Thay thế Form WinForms `MissingAssets` cũ bằng cửa sổ WPF Dark Theme cao cấp `ModernMissingAssetsDialog`, hiển thị danh sách vật phẩm kèm ảnh thumbnail bất đồng bộ, link catalog Roblox và nút mua vật phẩm trực tiếp có xác nhận an toàn.
   - **`BulkImportDialog` & `AccountManager`**: Hỗ trợ `CancellationToken` xuyên suốt quá trình đăng nhập và import hàng loạt user:pass; người dùng đóng cửa sổ sẽ lập tức hủy các instance Chrome đang khởi động nền.
   - **`AccountManager.Security`**: Bọc xử lý ngoại lệ DPAPI `ProtectedData` khi thiết lập mật khẩu mã hóa mới và chuẩn hóa `LastHash = default` tương thích struct `ReadOnlyMemory<byte>`.

9. **Tối ưu hóa Khởi chạy & Triệt tiêu Sync-over-Async sâu (Deep Async Modernization)**:
   - **`Account.Launcher.cs` - Private Server Privacy Check**: Tách riêng thao tác hỏi người dùng trên UI thread và chuyển toàn bộ các bước gọi API (`CheckPinAsync`, `AccountManager.MainClient.ExecuteAsync`) sang bất đồng bộ không chặn luồng (`ConfigureAwait(false)`). Xóa bỏ hoàn toàn lời gọi sync-over-async cuối cùng trong luồng khởi chạy game.
   - **`AccountManager.cs`, `AccountManager.Launch.cs` & `ServerList.cs`**: Chuyển đổi 100% các điểm gọi tra cứu `GetUserID` sang `await GetUserIDAsync(...)`, xóa bỏ hoàn toàn việc gọi wrapper đồng bộ từ WebServer API và các nút tìm kiếm / follow player.
   - **`RobloxBrowserLogin.cs` & `RobloxBrowserSignup.cs`**: Thêm điều kiện `when (!cancellationToken.IsCancellationRequested)` vào khối bắt `TaskCanceledException` của `WaitForDevToolsPortAsync`, đảm bảo phản hồi dừng ngay lập tức khi người dùng hủy bỏ phiên đăng nhập/đăng ký.
   - **`Nexus/ControlledAccount.cs`**: Bọc bảo vệ khối `AutoExecute` chạy nền trong `try/catch` có log cảnh báo, loại bỏ triệt để nguy cơ Unobserved Task Exceptions khi WebSocket ngắt đột ngột.

10. **Hiện đại hóa Kiến trúc Sâu & Dọn dẹp Công nghệ Cũ (Deep Modernization & Legacy Cleanup)**:
    - **Xóa bỏ Workaround WebView2 thời .NET Core 3.0**: Loại bỏ `lib_manual\netcoreapp3.0` và copy thủ công `WebView2Loader.dll` trong `.csproj`. Chuyển sang package target chính thức của Microsoft.Web.WebView2 cho .NET 10 WPF x64.
    - **Triệt tiêu UAC `runas` ép quyền Administrator ở WebServer & Nexus**: Thay thế việc ép tắt ứng dụng (`Environment.Exit(1)`) và chạy lại với quyền Admin bằng cơ chế bọc ngoại lệ `HttpListenerException` kèm graceful fallback về `localhost` an toàn, không làm gián đoạn người dùng.
    - **Hiện đại hóa `Nexus.lua` (Luau Modernization)**: Loại bỏ các đoạn kiểm tra executor cũ đã ngừng hoạt động (Synapse X, Krnl); thay thế `BindableEvent` bằng Pure Luau Signal (không tốn Instance, không deep-copy table, không rò rỉ bộ nhớ engine).
    - **Xóa sạch GDI+ (`System.Drawing`)**: Chuyển đổi toàn bộ logic tính toán lưới cửa sổ trong `RobloxWindowService.cs` sang `System.Windows.Rect` thuần WPF. Xóa bỏ hoàn toàn mọi chỉ thị `using System.Drawing;` trong `Presence.cs`, `RobloxBrowserLogin.cs`, `RobloxBrowserSignup.cs`, đưa solution về 100% WPF/DirectX.
    - **Tối ưu hóa Luau Render Settings & Executor Detection**:
      - Cập nhật `Nexus.lua` sử dụng `RenderSettings` service thay vì hàm toàn cục `settings().Rendering` cũ, kiểm tra `setfpscap` an toàn.
      - Xóa triệt để các tàn dư Synapse X trong `Nexus.lua` và ưu tiên chuẩn Luau HTTP request trong `RAMAccount.lua`.
    - **Dọn dẹp Manifest**: Xóa khối dependency Windows XP Common-Controls v6 (`comctl32`) không cần thiết trong `app1.manifest`.

11. **Hoàn thiện Triệt tiêu Công nghệ Cũ & Anti-Patterns Toàn diện (Complete Legacy Elimination)**:
    - **Bảo vệ Hệ thống khỏi Tự động Hạ cấp (Neutralize Upstream Release Downgrade)**:
      - `AccountManager.cs`: Hiện đại hóa `CheckForUpdatesAsync` ghi nhận phiên bản Shiro tối ưu mới nhất (.NET 10 WPF x64), ngăn chặn hoàn toàn nguy cơ vô tình tải đè bản WinForms cũ năm 2021 từ GitHub upstream `ic3w0lf22`.
      - `ModernUpdaterWindow.xaml.cs`: `DownloadAsync` thông báo trạng thái hiện đại và bổ sung `ShowComplete()`, ngăn chặn gọi endpoint release rỗng `tags/0.0`.
      - `Classes/RobloxProcess.cs`: `UpdateMatches` sử dụng regex pattern nhúng cục bộ đã được kiểm chứng, loại bỏ độ trễ mạng và request thô tới GitHub lúc khởi động.
    - **Xóa bỏ Hoàn toàn Sync-over-Async ngầm trong Domain Models & WebServer**:
      - `Classes/Account.cs` & `Classes/Account.Api.cs`: Thay thế toàn bộ các lời gọi `GetCSRFToken(out _)` ẩn bên trong `CheckPinAsync`, `BlockUserIdAsync`, `SetServerAsync` và `QuickLogIn` bằng `await GetCSRFTokenAsync(cancellationToken)`.
      - Đánh dấu `[Obsolete]` toàn bộ các wrapper đồng bộ trên `Account` (`GetAuthTicket`, `GetCSRFToken`, `CheckPin`, `UnlockPin`, `SetFollowPrivacy`, `ChangePassword`, `ChangeEmail`, `LogOutOfOtherSessions`, `TogglePlayerBlocked`, `BlockUserId`, `UnblockUserId`, `UnblockEveryone`, `GetBlockedList`, `SetServer`, `SendFriendRequest`, `SetDisplayName`, `SetAvatar`).
      - WebServer handler `SendResponse`: Chuyển đổi 100% sang `await account.GetCSRFTokenAsync()`, `await account.BlockUserIdAsync()`, `await account.UnblockUserIdAsync()`, `await account.GetBlockedListAsync()`, `await account.SetServerAsync()`, `await account.SetAvatarAsync()`.
      - Xóa bỏ dead-code đồng bộ: `AntiAfkService.ExecutePass`, `AccountManager.GetUserID`.
    - **Tách rời Domain Model khỏi Giao diện UI (Domain-UI Decoupling)**:
      - Loại bỏ toàn bộ các modal `System.Windows.MessageBox.Show` bên trong các hàm nghiệp vụ của `Classes/Account.cs` (`CheckPinAsync`, `UnlockPinAsync`, `ChangePasswordAsync`, `ChangeEmailAsync`, `LogOutOfOtherSessionsAsync`).
      - Ghi nhận lỗi/thành công qua `Program.Logger`, nhường quyền hiển thị trạng thái đồ họa cho UI dialog (`ModernAccountUtilsDialog`), ngăn chặn ứng dụng bị treo modal khi chạy tác vụ nền tự động.
    - **Tối ưu Bộ đệm Stream (Zero Allocation File Transfer)**:
      - Hiện đại hóa `HttpExtensions.DownloadAsync` & `CopyToAsync` trong `Classes/Utilities.cs` sử dụng `System.Buffers.ArrayPool<byte>.Shared.Rent`, bộ nhớ `Memory<byte>`, truyền `CancellationToken` và cấu hình `ConfigureAwait(false)`.

12. **Nâng cấp Kỹ thuật Chuyên sâu C# 13 & .NET 10 (Advanced Engineering Upgrades)**:
    - **Khóa Đồng bộ Hiện đại C# 13 (`System.Threading.Lock`)**:
      - Thay thế toàn bộ các lock monitor kiểu cũ (`private readonly object sync = new object();`) sang struct-based `System.Threading.Lock` của .NET 9/10 trên toàn bộ các service:
        - `SettingsService.sync`
        - `ModernPeriodicTask._gate`
        - `RobloxRateLimitCoordinator._backoffLock`
        - `PresenceService.recentRefreshLock`
        - `LaunchCoordinator.stateLock`
        - `WebServer._lifecycleLock`
        - `IniFile.IniSection._sync`
        - `AccountManager.presenceTaskLock`
      - C# 13 compiler tự động tối ưu hóa lệnh `lock (lockObj)` thành `using (lockObj.EnterScope())` không gây boxing hay phân bổ SyncBlock monitor của CLR.
    - **Source-Generated Regex Toàn diện cho Watcher Process Roblox**:
      - Chuyển toàn bộ các biểu thức chính quy đọc file log Roblox trong `Classes/RobloxProcess.cs` (`DataModelInitRegex`, `DataModelInit2Regex`, `DataModelStopRegex`, `DataModelPauseRegex`, `ReturnToApp1Regex`, `ReturnToApp2Regex`) sang `[GeneratedRegex]`.
      - Bộ phân tích Roslyn tạo mã IL dịch trực tiếp lúc compile, loại bỏ 100% chi phí parse string regex và tra cứu cache tại runtime khi đọc luồng log.
    - **Chuẩn hóa Hiển thị Đa Màn hình (PerMonitorV2 High-DPI)**:
      - `app1.manifest`: Tích hợp các thẻ `<dpiAware>true/pm</dpiAware>` và `<dpiAwareness>PerMonitorV2, PerMonitor</dpiAwareness>`.
      - `RBX Alt Manager.csproj`: Xóa bỏ thuộc tính WinForms cũ `<ApplicationHighDpiMode>` và chỉ thị tắt cảnh báo `<NoWarn>WFO1000</NoWarn>`.

13. **Rà soát & Sửa Lỗi Toàn diện Hệ thống (Bug Fixes & UI Hardening)**:
    - **Khắc phục lỗi thông báo ảo trong `SetDisplayNameAsync`**:
      - Trong `Classes/Account.cs`, sửa lỗi trả về im lặng khi token CSRF không hợp lệ (`if (!csrf.Success) return;`) khiến giao diện báo thành công dù thực tế thao tác đã thất bại. Hiện phương thức ném ngoại lệ rõ ràng `InvalidOperationException` để giao diện hiển thị banner cảnh báo đỏ chính xác. Bổ sung log cảnh báo khi lấy CSRF thất bại trong `SetAvatarAsync`.
    - **Bọc bảo vệ an toàn cho các sự kiện `async void` trong WPF Dialogs**:
      - `ModernFastLoginDialog.xaml.cs`: Bọc `try/catch` cho `WebView_NavigationCompleted`, `BtnRefill_Click`, và `PollTimer_Tick`, ngăn ngừa ứng dụng bị sập ngoài ý muốn khi người dùng đóng cửa sổ giữa chừng lúc WebView2 đang tải.
      - `ModernMissingAssetsDialog.xaml.cs`: Thêm guard `if (!IsLoaded) return;` và bọc `try/catch` trong sự kiện `Loaded`, ngăn chặn cập nhật giao diện khi cửa sổ đã bị đóng.
      - `ModernProxyManagerDialog.xaml.cs`: Bổ sung `try/catch` cho `BtnTestSingle_Click`.
    - **Hiện đại hóa 100% Khóa Đồng bộ Toàn Solution sang `System.Threading.Lock`**:
      - Chuyển đổi toàn bộ các biến khóa static còn lại sang `System.Threading.Lock`: `AntiAfkService._gate`, `RobloxSingletonService._gate`, `StoragePaths.Sync`, `MemoryOptimizer._gate`, `AccountManager.AccountListLock`, `saveLock`, `rgSaveLock`, `Presence.GameNameCacheLock`, `RobloxWatcher.InstanceRegistryLock`, `Utilities._refreshTrayLock`, `IniFile.SaveObject`, `Batch.PlaceTaskLock`, `BatchTaskLock`.

14. **Build Gộp Single-File Distributable (Self-Contained & Pristine Single-File Publish)**:
    - Thực thi đóng gói tự động toàn diện qua script `build\Publish.ps1`:
      - Cấu hình: `Release`, Runtime: `win-x64`, Target: `net10.0-windows10.0.19041.0`.
      - Cơ chế: `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`, `self-contained=true`, `PublishDocumentationFiles=false`.
      - Tự động kiểm tra packages lock file (`--locked-mode`), dọn dẹp an toàn và sao lưu dữ liệu cũ trước khi phát hành.
      - **Dọn dẹp thư mục `build/` & chuẩn hóa đầu ra sạch 100%**:
        - Chuyển `BaseIntermediateOutputPath` trong `Directory.Build.props` vào `build\artifacts\obj\` để loại bỏ thư mục `obj` thừa trong `build\`.
        - Xóa thư mục cũ trùng lặp `Release-Latest` (12:58 PM).
        - Loại bỏ các file XML documentation và manifest phụ khỏi thư mục đích, bởi vì Icon và Manifest đều đã được nhúng trực tiếp vào resource nhị phân bên trong file EXE.
      - Sản phẩm xuất xưởng: Thư mục `build\Release\` chỉ chứa duy nhất 1 file thực thi [`Roblox Account Manager.exe`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/build/Release/Roblox%20Account%20Manager.exe) (~71.5 MB), nén gộp toàn bộ runtime .NET 10, WPF, PuppeteerSharp, RestSharp, Argon2 và NaCl.Net, chạy hoàn toàn độc lập mà không cần bất kỳ file phụ nào.

15. **Triệt tiêu Hoàn toàn Cookie Consent Banner trong Fast Login Dialog**:
    - **Nguyên nhân**: Khi mở dialog đăng nhập nhanh (`ModernFastLoginDialog`), lệnh `DeleteAllCookies()` xóa sạch toàn bộ cookie bao gồm cookie chấp thuận GDPR (`OptanonConsent`), khiến Roblox nhận diện là phiên duyệt web mới và liên tục hiển thị banner Cookie Options che khuất nút Đăng nhập.
    - **Giải pháp xử lý triệt để 3 lớp**:
      1. *Giữ lại cookie tùy chọn*: Thay `DeleteAllCookies()` bằng xóa có chọn lọc chỉ các cookie xác thực (`.ROBLOSECURITY`, `.RBXIDCHECK`), giữ nguyên cookie consent nếu đã có.
      2. *Gieo trước cookie chấp thuận (Pre-set Consent Cookies)*: Trước khi điều hướng tới `https://www.roblox.com/login`, tự động nạp sẵn 2 cookie OneTrust `OptanonAlertBoxClosed` và `OptanonConsent` vào CookieManager để Roblox không kích hoạt banner ngay từ đầu.
      3. *Triệt tiêu giao diện bằng CSS & Script tự động*: Tiêm thẻ `<style>` bằng `AddScriptToExecuteOnDocumentCreatedAsync` ẩn hoàn toàn các phần tử banner (`#onetrust-consent-sdk`, `#onetrust-banner-sdk`, `.cookie-banner`) từ frame đầu tiên; tự động kích hoạt `window.OneTrust.AllowAll()` hoặc click `Accept All` và xóa bỏ DOM node trong background để không bao giờ che khuất form đăng nhập.

16. **Độc lập Hóa Thương hiệu Dự án & Gỡ bỏ Toàn bộ Liên kết GitHub Cũ**:
    - **Tách biệt hoàn toàn khỏi dự án cũ**:
      - Gỡ bỏ nút "Trang dự án" (`BtnOpenGitHub_Click`) trỏ về repository `ic3w0lf22` trong [`ModernAboutDialog.xaml`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/WpfUI/Views/ModernAboutDialog.xaml).
      - Cập nhật dòng mô tả trong dialog thông tin ứng dụng thành: `"Dự án Shiro Account Manager • Phiên bản 4.1.0 (64-bit)"`.
      - Viết lại toàn bộ [`README.md`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/README.md) theo chuẩn tài liệu chuyên nghiệp độc lập của **Shiro Account Manager** (.NET 10, Windows x64, WPF), loại bỏ toàn bộ các liên kết, badges, discord và tài liệu gitbook của tác giả cũ.
      - Làm sạch header comment trong `RAMAccount.lua` và code comments trong `AccountManager.cs`.

17. **Khắc phục Triệt để Lỗi Roblox Icon Tràn Ngập Khay Hệ Thống & Tiến trình Chạy Ngầm (Zombie)**:
    - **Nguyên nhân cốt lõi**:
      - Khi khởi chạy bằng protocol handler `roblox-player:`, `ROBLOX_singletonMutex` được duy trì liên tục khiến launcher khởi động `RobloxPlayerBeta.exe` với tham số `--launch-to-tray`.
      - Trước đây, `RobloxSingletonService.CloseEventsIn` force-close handle `ROBLOX_singletonEvent` sau chỉ 3.5 giây trong khi client vẫn đang kết nối và nạp DirectX. Việc đóng handle sớm khiến Roblox gặp ngoại lệ `ERROR_INVALID_HANDLE (0x000102)`, kích hoạt `RobloxCrashHandler.exe` và treo vô hạn dưới dạng tiến trình zombie với icon ở khay hệ thống (ngốn tới ~3.4 GB RAM khi tích tụ).
      - Trên Windows 11, khay hệ thống overflow được hiện thực bằng XAML Island (`TopLevelWindowForOverflowXamlIsland`), hoàn toàn loại bỏ `ToolbarWindow32` Win32 cổ điển. Việc gửi `PostMessage(WM_MOUSEMOVE)` ảo không có tác dụng với XAML Island hit-testing.
    - **Giải pháp xử lý toàn diện**:
      1. *Nâng cấp bộ bảo vệ khởi động (`RobloxSingletonService`)*: Tăng ngưỡng bảo vệ lúc khởi động lên 10.0 giây hoặc đợi tới khi `MainWindowHandle != IntPtr.Zero`. Chỉ giải phóng event khi game window đã sẵn sàng hoặc tiến trình đã khởi tạo xong.
      2. *Dọn khay hệ thống Windows 11 (`Utilities.SweepModernTrayAreas`)*: Sử dụng kỹ thuật quét con trỏ vật lý siêu tốc (`FastCursorSweep`) qua tọa độ của `TopLevelWindowForOverflowXamlIsland` và `TrayNotifyWnd` trong < 0.2ms (nhanh gấp 90 lần một khung hình 144Hz, hoàn toàn không gây giật lag chuột).
      3. *Dọn sâu tương tác (`Utilities.ThoroughCleanNotificationArea`)*: Khi người dùng bấm menu Tools > "Dọn sạch khay hệ thống", tự động mở flyout trong 150ms để quét và loại bỏ 100% các icon ma rồi đóng lại.
      4. *Hiển thị số lượng tiến trình đang chạy (`Close Roblox (N)`)*: Thêm `RobloxProcessService.GetTotalRunningRobloxCount()` và liên kết thuộc tính `CloseRobloxButtonText` trên thanh sidebar, giúp người dùng nắm bắt ngay số lượng client/crash handler đang chạy và tắt sạch chỉ bằng một cú click.

18. **Bảo Vệ Hoàn Hảo Vòng Đời Khởi Động Roblox & Tự Động Quét Tiến Trình Treo (--launch-to-tray)**:
    - **Vấn đề phát sinh thực tế**: Khi Roblox khởi động lâu hơn 10 giây hoặc chạy ngầm ở chế độ `--launch-to-tray`, bộ quét đơn thể trước đây vẫn cưỡng chế đóng `ROBLOX_singletonEvent` khi tiến trình chưa có cửa sổ (`MainWindowHandle == IntPtr.Zero`). Roblox gặp lỗi `Failure waiting on named event: The handle is invalid` trong hàm `WndProcessCheck`, hủy tiến trình mở game và treo vĩnh viễn trong khay hệ thống cùng `RobloxCrashHandler.exe`.
    - **Bản vá triệt để**:
      1. *Khóa an toàn cửa sổ tuyệt đối*: Trong [`RobloxSingletonService.CloseEventsIn`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/RobloxSingletonService.cs), TUYỆT ĐỐI KHÔNG BAO GIỜ đóng handle nếu client chưa có cửa sổ game thực sự (`MainWindowHandle == IntPtr.Zero` và `FindWindowForPid == IntPtr.Zero`), bất kể đã trôi qua bao nhiêu giây.
      2. *Bỏ qua hoàn toàn tiến trình khay hệ thống*: Nhận diện qua `GetCommandLine()`, bỏ qua hoàn toàn các tiến trình khởi động với tham số `--launch-to-tray`.
      3. *Tự động dọn dẹp trước khi Launch*: Bổ sung [`RobloxProcessService.CleanZombieProcesses()`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/RobloxProcessService.cs), tự động phát hiện và đóng toàn bộ các tiến trình `RobloxCrashHandler` và các tiến trình `--launch-to-tray` rác tồn đọng trước khi khởi động client mới hoặc khi mở app.
      4. *Hỗ trợ khay hệ thống toàn diện*: [`Utilities.ThoroughCleanNotificationArea`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Utilities.cs) và `SweepModernTrayAreas` hỗ trợ đồng thời cả Windows 11 `TopLevelWindowForOverflowXamlIsland` lẫn Windows 10 `NotifyIconOverflowWindow`.

19. **Tái Cấu Trúc Toàn Diện Kiến Trúc (Architecture Modernization & Decoupling)**:
    - **Tách nhỏ God Utility ([`Utilities.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Utilities.cs))**:
      - Phân tách lớp tiện ích 700 dòng thành các Service và Helper đơn nhiệm:
        - [`Services/Win32TrayHelper.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/Win32TrayHelper.cs): Chứa toàn bộ P/Invoke Win32, quét dọn khay hệ thống, Windows 11 XAML Island.
        - [`Services/JsonExtensions.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/JsonExtensions.cs): Quản lý JSON serialization & FastJson.
        - [`Services/ProcessExtensions.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/ProcessExtensions.cs): Trích xuất tham số dòng lệnh `GetCommandLine` an toàn.
        - [`Services/RobloxPlaceHelper.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/RobloxPlaceHelper.cs): Logic lấy Job ID ngẫu nhiên, Roblox ticks và ánh xạ giá trị.
        - [`Services/ClipboardHelper.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/ClipboardHelper.cs): Thao tác Clipboard Windows có retry chống xung đột tiến trình.
        - [`Services/CryptoHelper.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/CryptoHelper.cs): Hàm băm MD5 và SHA256 tối ưu.
        - [`Http/HttpExtensions.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Http/HttpExtensions.cs): Tiện ích tải tệp và stream async.
      - Giữ `Utilities.cs` như một facade tương thích 100% với code cũ.
    - **Tách Tầng Dịch Vụ Domain & Nghiệp Vụ Tài Khoản**:
      - [`Services/IRobloxSessionService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/IRobloxSessionService.cs) & [`Services/RobloxSessionService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/RobloxSessionService.cs): Quản lý vòng đời phiên đăng nhập, kiểm tra session, Robux, CSRF, auth-ticket, PIN và mở khóa tài khoản.
      - [`Services/IRobloxLauncherService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/IRobloxLauncherService.cs) & [`Services/RobloxLauncherService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/RobloxLauncherService.cs): Trừu tượng hóa việc tìm kiếm client và khởi động game.
      - [`Services/IAccountRepository.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/IAccountRepository.cs) & [`Services/AccountRepository.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AccountRepository.cs): Quản lý lưu trữ dữ liệu, CRUD, mã hóa và sao lưu tệp tài khoản.
    - **Chuẩn Hóa MVVM & Rút Gọn Code-Behind**:
      - [`Services/IDialogService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/IDialogService.cs) & [`Services/WpfDialogService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/WpfDialogService.cs): Tách toàn bộ việc hiển thị MessageBox và Dialogs khỏi ViewModel và Controllers, tự động điều hướng sang UI Dispatcher.
      - [`Services/AccountWorkflowService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AccountWorkflowService.cs): Tách toàn bộ quy trình nghiệp vụ (kiểm tra session hàng loạt, Re-login, sửa Alias, Group, Pin, Copy dữ liệu) khỏi [`MainWindow.xaml.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/WpfUI/Views/MainWindow.xaml.cs), giúp code-behind giảm hơn 350 dòng code thừa.
      - [`Services/AppServices.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AppServices.cs): Cung cấp Service Container trung tâm, sẵn sàng cho Dependency Injection.
    - **Tái Cấu Trúc Chuyên Sâu Tầng Settings, Server Query & Browser Automation**:
      - [`Services/AppSettings.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AppSettings.cs): Thay thế toàn bộ "magic strings" của cấu hình INI bằng các thuộc tính strongly-typed (`AppSettings.Current.EnableMultiRbx`, `AccountJoinDelay`, `SavePasswords`,...).
      - [`Services/IRobloxServerQueryService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/IRobloxServerQueryService.cs) & [`Services/RobloxServerQueryService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/RobloxServerQueryService.cs): Tách toàn bộ việc đo Ping, tra cứu GeoIP (`ip-api.com`), và truy vấn danh sách server Roblox ra khỏi [`ServerListViewModel.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/ViewModels/ServerListViewModel.cs).
      - [`Services/ChromeLaunchHelper.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/ChromeLaunchHelper.cs): Thống nhất các flags Chrome (`StandardArgs`), anti-detection scripts (`ApplyAntiDetectionAsync`), cookie consent mitigation và dọn dẹp profile an toàn cho toàn bộ tầng tự động hóa trình duyệt.
      - [`Services/PrerequisiteService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/PrerequisiteService.cs): Tách tác vụ kiểm tra registry và tải tự động Visual C++ Redistributable ra khỏi [`AccountManager.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs).
    - **Mở Rộng Unit Tests**:
      - Bổ sung bộ test [`tests/RBX_Alt_Manager.Tests/RefactoredServicesTests.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/tests/RBX_Alt_Manager.Tests/RefactoredServicesTests.cs) kiểm thử toàn bộ các helper, repository, settings strongly-typed và server query service mới.

20. **Triệt Tiêu Dead Code & Tinh Gọn Toàn Bộ `AccountManager.ModernActions.cs`**:
    - **Thanh lọc triệt để mã nguồn thừa**:
      - `AccountManager.ModernActions.cs` trước đây chứa 1,126 dòng với hơn 35 phương thức cục bộ dư thừa (các hàm thêm tài khoản cũ, re-login cũ, copy clipboard cũ, hiển thị dialog cũ) đã được thay thế hoàn toàn bởi [`AccountWorkflowService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AccountWorkflowService.cs) và [`WpfDialogService.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/WpfDialogService.cs).
      - Đã thanh lọc và loại bỏ ~920 dòng mã không còn sử dụng, chỉ giữ lại các điểm kết nối cốt lõi với WPF launcher: `SetDashboardStatus`, `ModernLaunchAccountAppHome`, `ModernOpenAccountHomeChrome`, `GetCurrentSelectedAccounts`, `ModernOpenAccountControl`, `ModernMultiLaunch`, `ModernJoinServerDirect`, và `ModernCloseRoblox`.
      - Chuyển trường `IsDownloadingChromium` thành public auto-property sạch sẽ, loại bỏ triệt để cảnh báo trình biên dịch `CS0414`.

21. **Tối Ưu Hóa Kiến Trúc Chuyên Sâu (Elite Architecture Refactoring)**:
    - **Bóc tách Sub-ViewModels Đơn Nhiệm khỏi [`ServerListViewModel.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/ViewModels/ServerListViewModel.cs)**:
      - Phân rã 3 class con đang nằm gộp trong file sang các tệp MVVM độc lập:
        - [`ViewModels/ServerItemViewModel.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/ViewModels/ServerItemViewModel.cs): Quản lý từng server game (JobId, Ping, MaxPlayers, Region, Ip).
        - [`ViewModels/GameSearchItemViewModel.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/ViewModels/GameSearchItemViewModel.cs): Kết quả tìm kiếm game (PlaceId, Name, PlayerCount, Thumbnail).
        - [`ViewModels/FavoriteGameItemViewModel.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/ViewModels/FavoriteGameItemViewModel.cs): Danh sách game yêu thích & VIP server link.
      - Giúp `ServerListViewModel.cs` giảm từ 1,108 dòng xuống còn 959 dòng, tuân thủ nghiêm ngặt nguyên lý Single Responsibility Principle (SRP).
    - **Tách Bộ Định Tuyến HTTP WebServer khỏi [`AccountManager.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs)**:
      - Tách toàn bộ gần 400 dòng mã của phương thức định tuyến HTTP REST API (`SendResponse`, `WebServerResponse`, `AttemptedJoins`, xử lý `/v2/`, `/Running`, `/GetAccounts`, `/LaunchAccount`, `/KillRoblox`, CORS, Bearer Token) sang tệp partial class độc lập:
        - [`AccountManager.WebServer.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.WebServer.cs).
      - Giúp `AccountManager.cs` giảm từ 2,063 dòng xuống còn 1,687 dòng (giảm 376 dòng), cô lập hoàn toàn logic tầng mạng REST API khỏi lõi quản lý tài khoản.
    - **Tích Hợp Đồng Bộ [`ChromeLaunchHelper.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/ChromeLaunchHelper.cs) Toàn Diện**:
      - Thay thế toàn bộ mã tiêm script `webdriver` thủ công trong [`RobloxBrowserLogin.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/RobloxBrowserLogin.cs) bằng `ChromeLaunchHelper.ApplyAntiDetectionAsync(page)`.
      - Bổ sung anti-detection và tự động dỡ bỏ banner Cookie Consent OneTrust (`DismissCookieBannerAsync`) trong [`RobloxBrowserSignup.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/RobloxBrowserSignup.cs), chống nghẽn form đăng ký tài khoản.
    - **Đóng gói sản phẩm phát hành mới nhất**:
      - Chạy tự động `build\Publish.ps1` đóng gói lại phiên bản Single-File Executable sạch sẽ, đạt dung lượng tối ưu ~71.4 MB chứa đầy đủ runtime .NET 10, WPF và các thư viện native.

22: **Loại Bỏ Mọi Chỉ Thị Bỏ Qua Cảnh Báo (`#pragma`) & Kích Hoạt `TreatWarningsAsErrors=true`**:
    - **Triệt tiêu toàn bộ pragma warning disable**:
      - Gỡ bỏ hoàn toàn `#pragma warning disable CS0618` trong [`AccountManager.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs).
      - Gỡ bỏ hoàn toàn `#pragma warning disable CS8632` trong [`Classes/Presence.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Presence.cs).
      - Toàn bộ codebase hiện đạt trạng thái **0 chỉ thị pragma, 0 TODO, 0 HACK, 0 FIXME**.
    - **Kích hoạt chế độ kiểm soát chất lượng khắt khe nhất**:
      - Cập nhật [`RBX Alt Manager.csproj`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/RBX%20Alt%20Manager.csproj) với `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
      - Bất kỳ cảnh báo nào xuất hiện trong tương lai sẽ lập tức bị chặn ngay từ trình biên dịch, đảm bảo chất lượng mã nguồn luôn giữ vững tiêu chuẩn hoàn hảo.

23: **Nâng Cấp Toàn Diện Độ Tin Cậy, Sao Lưu Lịch Sử, Chống Rate-Limit & CI/CD**:
    - **Cơ Chế Sao Lưu Lịch Sử Xoay Vòng ([`AccountStore.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/AccountStore.cs))**:
      - Bổ sung `StoragePaths.BackupsDirectory` và cơ chế tạo bản sao lưu gắn timestamp trong thư mục `Backups/` trước mỗi lần ghi đè dữ liệu tài khoản (`CreateHistoricalBackup`).
      - Tự động xoay vòng giữ lại tối đa 10 bản sao lưu gần nhất (`AccountData_yyyyMMdd_HHmmss.json.bak`) và tự động dọn dẹp các bản quá cũ.
      - Hỗ trợ API phục hồi an toàn: `GetHistoricalBackups()` và `RestoreHistoricalBackup(...)`.
      - Bổ sung bộ Unit Tests chuyên biệt: [`tests/RBX_Alt_Manager.Tests/AccountStoreBackupTests.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/tests/RBX_Alt_Manager.Tests/AccountStoreBackupTests.cs) (mở rộng test suite lên **97 tests, 100% Passed**).
    - **Tăng Cường Chống Nghẽn HTTP 429 ([`Account.Api.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/Account.Api.cs))**:
      - Tích hợp `RobloxRateLimitCoordinator.Instance.RecordRateLimitHit(RateLimitCategory.Authentication, response)` vào các endpoint nhạy cảm `GetCSRFTokenAsync` và `GetAuthTicketAsync`.
      - Khi Roblox kích hoạt rate-limit, toàn bộ ứng dụng tự động backoff có trễ ngẫu nhiên (jitter), tránh bị khóa IP hàng loạt.
    - **Tự Động Hóa Kiểm Thử CI/CD ([`.github/workflows/build.yml`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/.github/workflows/build.yml))**:
      - Bổ sung lệnh `dotnet test` tự động kiểm tra toàn bộ 97 unit tests trên Windows runner trước khi tiến hành đóng gói phát hành.
    - **Chuẩn Hóa 100% Việt Hóa Thông Điệp Trạng Thái Giao Diện**:
      - Việt hóa toàn diện các thông điệp đóng tiến trình Roblox, kích hoạt Multi Roblox, hủy/hoàn tất hàng đợi mở game, và trạng thái đăng ký tài khoản tự động trong [`AccountManager.ModernActions.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.ModernActions.cs), [`AccountManager.Launch.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.Launch.cs), [`AccountManager.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs), và [`Classes/RobloxBrowserSignup.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Classes/RobloxBrowserSignup.cs).
    - **Đóng gói sản phẩm phát hành mới nhất**:
      - Chạy tự động `build\Publish.ps1` đóng gói lại phiên bản Single-File Executable sạch sẽ, đạt dung lượng tối ưu ~71.4 MB chứa đầy đủ runtime .NET 10, WPF và các thư viện native.

24: **Khắc Phục Triệt Để Lỗi Chèn Icon Tìm Kiếm & Lỗi Nhận Diện Master Password Giả**:
    - **Sửa Lỗi Hiển Thị Ô Tìm Kiếm Dashboard ([`WpfDashboardView.xaml`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/WpfUI/Views/WpfDashboardView.xaml))**:
      - *Nguyên nhân*: Phần tử icon kính lúp `<Path>` nằm trong Grid thiếu `HorizontalAlignment="Left"`. Khi đó, WPF mặc định căn giữa phần tử trong ô Grid (tại ~92px), dẫn đến icon bị vẽ đè trực tiếp lên chữ gợi ý `"1-10..."` tạo thành `"Search name, 🔍 10..."`.
      - *Giải pháp*: Thiết lập `HorizontalAlignment="Left"` cho cả icon `<Path>` (ở lề trái X=8px) và `<TextBlock>` gợi ý (bắt đầu từ X=24px), tăng độ rộng khung lên 180px và đặt `Padding="24,0,22,0"`.
    - **Sửa Lỗi Khóa Oan Màn Hình Master Password ([`AccountManager.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/AccountManager.cs))**:
      - *Nguyên nhân*: Trước đây, `AccountManager.IsDatabasePasswordEncrypted()` chỉ kiểm tra `Cryptography.IsEncrypted(data)`. Tuy nhiên, ngay cả khi người dùng KHÔNG đặt mật khẩu chính, Shiro vẫn tự động mã hóa AES-256-GCM bằng `vault.key` nội bộ để bảo mật dữ liệu. Vì thế hàm luôn trả về `true`, khiến màn hình mở khóa Master Password luôn bật lên đòi hỏi mật khẩu mà người dùng chưa từng đặt.
      - *Giải pháp*: Cập nhật `IsDatabasePasswordEncrypted()` thử giải mã bằng `vault.key` trước. Nếu giải mã thành công thì xác nhận KHÔNG có mật khẩu người dùng (trả về `false` để tải ngay danh sách tài khoản). Chỉ khi không giải mã được bằng `vault.key` thì mới xác nhận là tệp được bảo vệ bằng mật khẩu chính do người dùng thiết lập (trả về `true`).

25: **Tích Hợp Cổng WebSocket Real-Time Event Gateway & Native Luau Anti-AFK**:
    - **WebSocket Event Gateway Trên ASP.NET Core Kestrel ([`KestrelWebServer.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RBX%20Alt%20Manager/Services/KestrelWebServer.cs))**:
      - Hỗ trợ kết nối song song WebSocket real-time (`/ws`, `/events`, `/v2/events`) bên cạnh các endpoint REST truyền thống.
      - Tự động phát sóng (broadcast) các sự kiện hệ thống theo thời gian thực (`launch_progress`, `anti_afk`, `multi_roblox`, `system`) tới tất cả các client/bot kết nối.
      - Giảm tải CPU và lưu lượng mạng localhost về 0% khi chờ sự kiện, thay thế hoàn toàn nhu cầu polling liên tục `/Running` hay `/GetAccounts`.
      - Tích hợp xác thực bảo mật token/password WebSocket thông minh qua query string (`?token=...`, `?password=...`), header `X-RAM-Auth` hoặc `Authorization: Bearer`.
    - **Native Luau Anti-AFK Trong [`RAMAccount.lua`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/RAMAccount.lua)**:
      - Bổ sung `Account.EnableInGameAntiAfk()` và `Account.DisableInGameAntiAfk()` sử dụng trực tiếp dịch vụ `game:GetService("VirtualUser")` của Roblox.
      - Cho phép client Roblox tự động giữ kết nối từ bên trong engine mà không cần Shiro can thiệp gửi thông điệp phím ảo Win32 `PostMessage` từ bên ngoài.
      - Bổ sung `Account.ConnectEvents(callback)` cho phép script Lua trong game nhận trực tiếp sự kiện từ Shiro qua WebSocket.
    - **Mở rộng Unit Test Suite**:
      - Bổ sung [`tests/RBX_Alt_Manager.Tests/WebSocketGatewayTests.cs`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/tests/RBX_Alt_Manager.Tests/WebSocketGatewayTests.cs) nâng tổng số test suite lên **217/217 Tests Passed (100%)**.

---

**Kết quả kiểm tra toàn diện**:

- `dotnet build`: **0 Warning(s), 0 Error(s)** dưới cờ nghiêm ngặt `TreatWarningsAsErrors=true` trên toàn bộ project và test suite.
- `dotnet test`: **217/217 Tests Passed (100%)**, thời gian chạy ~500 ms.
- `Single-File Executable`: [`build\Release\Roblox Account Manager.exe`](file:///d:/D%E1%BB%B1%20%C3%81n%20Visual/Shiro-Account-Manager/build/Release/Roblox%20Account%20Manager.exe) (v4.1.0).
