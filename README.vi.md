# DocManager

Ứng dụng desktop Windows viết bằng C#/.NET 8 cho bốn luồng công việc: **DiaLux → Excel**, **Catalogue Downloader** (Signify, kèm IES → LDT), **LDT Editor** và **CAD Symbol**. Đây là ứng dụng .NET native; không phụ thuộc vào Python, không gọi script Python và không chỉnh sửa mã nguồn Python cũ.

## Yêu cầu, build và chạy Debug

Yêu cầu Windows 10/11 và .NET 8 SDK. Các lệnh dưới đây chỉ dùng cấu hình Debug:

```powershell
dotnet restore D:\Claude\DocManager\DocManager.sln
dotnet build D:\Claude\DocManager\DocManager.sln -c Debug --no-restore
dotnet run --project D:\Claude\DocManager\src\DocManager.Desktop\DocManager.Desktop.csproj -c Debug --no-build
dotnet test D:\Claude\DocManager\DocManager.sln -c Debug --no-build
```

Lệnh `dotnet build` thông thường vẫn xuất vào `bin` của từng project; nó không tạo/cập nhật bản portable trong `App`.

## Publish portable vào App

Phương thức chính thức để tạo bản portable là script PowerShell ở root solution. Trước khi chạy, đóng `DocManager.Desktop.exe` để các binary không bị khóa:

```powershell
pwsh -NoProfile -File D:\Claude\DocManager\build_app.ps1 -Configuration Release -RuntimeIdentifier win-x64
```

Build chính thức deploy theo thứ tự vào `D:\Claude\DocManager\App` (primary), rồi vào `\\ADMIN\Public\y.DocManager\App` (secondary). Cả hai destination đều chạy cùng deploy managed: chỉ thay/xóa payload do manifest sở hữu, seed dữ liệu chỉ bổ sung file còn thiếu, đặt launcher sau payload `Released` và ghi manifest sau cùng. Không có thư mục transaction/staging nào được copy vào App. Trước khi thay đổi App local, script kiểm tra UNC, rồi thực hiện probe best-effort create/write/rename/delete bằng file GUID tạm ngay trong App secondary nếu đã tồn tại, hoặc parent của nó nếu chưa tồn tại; probe luôn được xóa trước khi tiếp tục. Sau đó script preflight collision, manifest, lock và seed để lỗi network thông thường không làm commit App local. Đây không thể loại trừ ACL riêng từng file, lock, ngắt kết nối hoặc race phát sinh sau preflight: trong các lỗi muộn đó, App local có thể đã giữ build mới và script báo rõ trạng thái này để chạy lại sau. Khi offline, dùng switch tường minh sau để chỉ deploy local:

```powershell
pwsh -NoProfile -File D:\Claude\DocManager\build_app.ps1 -Configuration Release -RuntimeIdentifier win-x64 -SkipNetworkDeployment
```

Có thể kiểm tra trước mà không restore, publish, copy hoặc xóa bất kỳ tệp nào:

```powershell
pwsh -NoProfile -File D:\Claude\DocManager\build_app.ps1 -DryRun
```

Script chỉ cần PowerShell 7 và .NET 8 SDK; không cần Visual Studio C++ Build Tools, `cl`, `clang` hoặc MinGW. Script publish project `DocManager.Desktop` self-contained `win-x64` dạng multi-file vào staging sạch `artifacts\publish\DocManager.Desktop`, xác thực EXE/DLL/deps/runtimeconfig và bỏ PDB mặc định. Sau đó script publish `tools\DocManager.Launcher\DocManager.Launcher.csproj` riêng vào staging sạch `artifacts\publish\DocManager.Launcher` dưới dạng **self-contained single-file win-x64**, `WinExe`, không trim và không có DLL/JSON/PDB sidecar. Launcher root dùng runtime .NET tối thiểu nhúng bên trong chính EXE nên lớn hơn launcher C thuần, nhưng vẫn portable và tin cậy trên máy không cài .NET; không có external runtime hay DLL cạnh launcher ở root. Launcher tìm thư mục chứa chính nó, chạy đường dẫn tuyệt đối `Released\DocManager.Desktop.exe` với working directory là `Released`, chuyển tiếp chính xác từng argument (kể cả khoảng trắng, quote, Unicode, backslash cuối và argument rỗng), chờ và trả exit code của app; thiếu payload hoặc lỗi start sẽ hiện hộp thoại Windows có hướng dẫn. Trước deploy, build script chạy hidden runtime probe để xác nhận marker, process path, `AppContext.BaseDirectory`, target `Released` và kiến trúc x64 của đúng launcher đã stage. Icon mặc định của Windows được dùng vì repository hiện chưa có icon asset. Dùng `-KeepSymbols` nếu cần deploy PDB của payload Desktop; launcher staging luôn chỉ có một EXE. Dùng `-SkipRestore` nếu cả hai project đã được restore đúng RID/properties.

Layout runtime sau publish:

```text
App\
  DocManager.Desktop.exe                 launcher .NET self-contained single-file x64
  Released\
    DocManager.Desktop.exe               app .NET thật
    *.dll / *.json / runtime và native publish files
  Product Family\                       seed được copy, dữ liệu hiện hữu được giữ
  Product excel file\                    seed được copy, dữ liệu hiện hữu được giữ
    Prof Pricelist V1.0 2026 effective_Mar2026.xlsx
  .docmanager-publish-manifest.json       manifest version 2
```

`App` là portable root; cần di chuyển/copy **nguyên thư mục `App`** để launcher, `Released` và dữ liệu vẫn giữ quan hệ tương đối. `App\Released` nay là payload build được quản lý, không còn là thư mục legacy đứng ngoài deploy. Manifest version 2 chỉ sở hữu launcher root và các publish file dưới `Released`; tuyệt đối không sở hữu `Product Family` hoặc `Product excel file`. Mỗi build chỉ thay/xóa file manifest sở hữu, giữ file lạ trong `Released` và từ chối collision không chứng minh được. Khi migrate manifest version 1, script chỉ xóa payload root do manifest cũ sở hữu; binary root không có manifest được xem là không rõ chủ sở hữu và làm preflight thất bại thay vì bị xóa. Trong `Released` pre-v2 không có manifest v2, script chỉ tự adopt collision có hash trùng publish hiện tại; collision khác byte dừng an toàn, còn file lạ không trùng được giữ. Có thể dùng `-AdoptLegacyReleased` chỉ khi người vận hành đã xác minh và chủ động cho phép overwrite các collision của payload `Released` cũ.

Mặc định script seed đệ quy từ đúng `Product Family` và `Product excel file` cạnh script sang hai thư mục cùng tên dưới `App`: chỉ tạo directory/file còn thiếu, không thay đổi dù chỉ một byte của file đích đã tồn tại và không xóa file chỉ có ở đích. Source/destination reparse point và collision file-với-directory đều làm preflight thất bại. Dùng `-SkipDataCopy` để bỏ riêng bước seed. Publish, launcher và data mới được chuẩn bị trong transaction; payload cũ được backup, launcher chỉ được đặt sau `Released`, manifest được thay sau cùng, và lỗi giữa chừng kích hoạt rollback. Luôn đóng `DocManager.Desktop.exe` trước build để preflight lock không dừng thao tác.

Có thể kiểm tra deploy hoàn toàn trong temp bằng hai staging đã publish, không đụng `App` thật:

```powershell
pwsh -NoProfile -File D:\Claude\DocManager\build_app.ps1 `
  -SkipPublish `
  -StagingDirectory C:\Temp\DocManager-Desktop-Staging `
  -LauncherStagingDirectory C:\Temp\DocManager-Launcher-Staging `
  -AppDirectory C:\Temp\DocManager-App `
  -SkipNetworkDeployment
```

Với `-SkipPublish`, `-LauncherStagingDirectory` là tùy chọn; nếu bỏ qua, script dùng mặc định xác định `artifacts\publish\DocManager.Launcher`. Launcher staging phải chỉ có đúng `DocManager.Desktop.exe` và phải vượt hidden runtime probe; script không chấp nhận một PE hệ thống bất kỳ. `-NetworkAppDirectory` mặc định là `\\ADMIN\Public\y.DocManager\App`; chỉ dùng nó với temporary local path trong test harness cô lập, còn build vận hành dùng đích UNC mặc định. Các tham số `-ProductFamilySourceDirectory`, `-ProductExcelSourceDirectory` và `-TestFailAfterAppCreation` chỉ phục vụ test harness cô lập với fixture tổng hợp; build vận hành không dùng chúng. Manifest version 2 giữ nguyên schema/semantics, không có metadata riêng cho test launcher. `-DryRun` không restore, publish, chạy launcher, deploy payload, copy seed, xóa payload hoặc tạo directory; nếu cả hai staging hiện hữu thì script chỉ tính plan từ cấu trúc file mà không chạy probe launcher, còn nếu chưa có thì báo chính xác hai lệnh publish dự kiến và phần chỉ có thể tính sau publish. Khi không dùng `-SkipNetworkDeployment`, dry run vẫn chạy cùng preflight availability/authorization secondary (bao gồm probe create/write/rename/delete tạm, sau đó xóa probe) và đọc destination để tính plan, nhưng không deploy production payload; dùng switch này khi offline để chỉ xem plan local.

Test PowerShell publish launcher thật và một synthetic child hoàn toàn vào temp (bao gồm output trung gian `bin`/`obj`), rồi kiểm tra probe, missing target, argument/working-directory/exit-code forwarding, data preservation, migration v1, stale cleanup, collision, rollback và secondary destination giả lập local (manifest, payload replacement/stale cleanup, launcher layout và seed preservation), không đụng UNC mặc định:

```powershell
pwsh -NoProfile -File D:\Claude\DocManager\tests\build_app.temp.tests.ps1
```

## Kiến trúc solution

`DocManager.sln` gồm bảy project (sáu project ứng dụng/test và một tool launcher):

- `src/DocManager.Core`: model dùng chung, chuẩn hóa text và số, CCT nghiêm ngặt, `ProductAssetName` cho canonical product identity/tên asset Windows, repository CAD symbol theo user và thao tác tệp nguyên tử.
- `src/DocManager.Dialux`: trích xuất PDF DiaLux, parser report/catalogue, khớp variant và xuất/append Excel.
- `src/DocManager.Signify`: đọc pricelist, chấm điểm/kết quả Signify, tải catalogue/IES, layout thư mục và CSV log.
- `src/DocManager.Photometry`: parser IES LM-63, trích metadata catalogue, chuyển IES sang Eulumdat/LDT và model editor LDT.
- `src/DocManager.Desktop`: WPF shell, điều phối tác vụ bất đồng bộ, hủy, kéo/thả, tiến độ và log.
- `tests/DocManager.Tests`: xUnit; fixture cục bộ/tạm thời và fake HTTP, không gọi mạng Signify thật.
- `tools/DocManager.Launcher`: launcher `WinExe` .NET 8 self-contained single-file x64 đặt ở root portable; chỉ dùng BCL/PInvoke `MessageBoxW`, không phụ thuộc project ứng dụng hoặc compiler native ngoài .NET SDK.
- `tests/DocManager.Launcher.TestChild` không nằm trong solution/runtime production; đây là synthetic single-file child được `build_app.temp.tests.ps1` publish riêng vào temp để chứng minh argument/working-directory/exit-code forwarding.

## Bốn tab của ứng dụng

### DiaLux → Excel

Chọn hoặc kéo/thả nhiều PDF DiaLux, xuất từng workbook hoặc gộp, quét catalogue PDF để enrich dữ liệu, kiểm tra mã catalogue còn thiếu và chuyển các mã/tên đó sang tab Downloader. Tab cũng có thể dò form Excel hiện hữu rồi append theo header đã nhận diện, giữ style dòng mẫu và bỏ bản ghi trùng theo các trường schedule.

`Lighting Schedule` mới dùng 16 cột A:P, group header, màu/border/merge room, freeze/filter và công thức tổng/hiệu suất. Từ G đến P lần lượt là `Symbol in DWG`, `Proposed calculation model`, `Parameter`, `Reference image`, `Quantity`, `Power`, `Total power`, `Luminous flux`, `Efficiency` và `IP / IK`; công thức là M = K × L và O = N ÷ L. Workbook mới không còn cột `Type (Lamp symbol)`; append vào form cũ vẫn nhận diện cột Type nhưng để nguyên/để trống cột đó và map các cột còn lại theo header. Mỗi luminaire product của cùng một AREA chiếm một data row riêng: các trường cấp phòng A:F được merge dọc qua toàn bộ product rows, còn G:P (symbol, model, parameter, ảnh, quantity, power, công thức và thông số sản phẩm) luôn giữ riêng từng row. Cột B `AREA` giữ tên phòng/khu vực; ground area và LPD được giữ riêng trong model chứ không ghi đè tên phòng. Parser dựng lại thứ tự đọc trực quan từ positioned words (không dựa vào thứ tự content stream), đồng thời dùng tọa độ X của header `Calculated`/`Target`/`Index` để đọc result grid khi nhãn Working plane/Ē/Uo bị tách token hoặc tách visual row. Trang summary chỉ bổ sung ground area/LPD cho room có result-grid hoặc Calculation object chi tiết; level summary không có evidence chi tiết không tạo pseudo-room. Luminaire list trên cùng room page hoặc trang summary/list liền kề có cùng heading được gắn cục bộ theo định danh phân cấp đầy đủ (không gộp hai block có cùng tên phòng lá, và vẫn phân biệt dấu câu có nghĩa như `R-1`/`R1`), chịu được khác biệt dấu tiếng Việt/khoảng trắng do glyph lệch baseline, và bỏ trùng theo article + designation đầy đủ; association bị chặn theo section trang liên tiếp để không rải list sang phòng khác. Inventory toàn report chỉ fallback cho báo cáo đúng một room, không rải qua nhiều room. Các fallback text-row/table/summary/legacy vẫn hỗ trợ số theo cả định dạng `1.234,56` lẫn `1,234.56`. Khi catalogue có ảnh raster sản phẩm hợp lệ trên trang 1, service ảnh trước hết tìm annotation FreeText/Typewriter có nội dung chuẩn hóa `Reference image`; nếu có, Square/Rectangle màu đỏ gần nhất chỉ được dùng để chọn raster có overlap tốt nhất, **không** dùng làm tọa độ crop. Annotation hoàn toàn không bắt buộc: PDF không có/không đọc được annotation vẫn dùng fallback an toàn, ưu tiên ảnh hiển thị lớn nhất ngoài vùng logo góc trên-trái, hỗ trợ cả raster JPEG nhúng, thử ứng viên tiếp theo nếu decode thất bại và luôn loại artwork phủ gần toàn trang để không xuất nguyên datasheet giả làm ảnh sản phẩm. Raster trang 1 đã chọn được crop thích ứng theo foreground pixel thực: mô hình nền lấy từ median/percentile của border, hỗ trợ nền gần trắng hoặc trong suốt, dung sai nhiễu JPEG/màu xám sáng; connected components nhỏ bị bỏ như bụi nén, còn union các component đáng kể giữ sản phẩm nhiều phần, cạnh mảnh và shadow. Bounding box được thêm padding 5% (tối thiểu 8 px), clamp trong ảnh; khi độ tin cậy thấp service giữ nguyên raster thay vì fixed inset có thể cắt sản phẩm. Nền alpha và tỷ lệ khung hình được bảo toàn, chỉ downscale đồng đều, đầu ra PNG tối đa 1600 px. Kết quả được cache LRU theo đường dẫn/kích thước/timestamp, tối đa 128 entry và 64 MiB PNG (kết quả `null` cũng chịu giới hạn số entry); đường dẫn đã xóa hoặc timestamp thay đổi làm entry cũ bị loại. Ảnh sau đó được lưu vào `CatalogueEntry`; lỗi ảnh chỉ trả `null`, không làm hỏng metadata/index. Excel chèn ảnh vừa khít trong cột **Reference image** (J ở schedule mới hoặc cột `refimg` được dò trong form), tăng row/column tối thiểu khi cần; model compound ưu tiên ảnh main rồi accessory và xếp tối đa hai ảnh trong cùng cell. Append bỏ qua ảnh nếu form không có `refimg`, và dòng schedule trùng không sinh thêm drawing; dedupe của các dòng mới dùng định danh phòng phân cấp đầy đủ được lưu trong worksheet metadata `__DocManagerMetadata` ở trạng thái very-hidden để used range của schedule mới dừng đúng A:P. Workbook/form cũ vẫn tiếp tục dùng cột metadata ẩn `__DocManagerRoomIdentity` nếu có, còn workbook cũ chưa có metadata được xử lý bảo thủ theo AREA hiển thị. Room mới có nhiều product cũng merge các cột room-level được form nhận diện, còn trường hợp chỉ append một product mới vào room đã tồn tại sẽ lặp lại room fields trên row mới để tránh merge không liền kề. Khi gộp nhiều report trong một workbook mới, cột `No.` tăng liên tục qua ranh giới report thay vì bắt đầu lại từ 1.

### Catalogue Downloader

Nạp pricelist XLSX, nhập một mã mỗi dòng hoặc ngăn cách bằng dấu phẩy, nhận gợi ý từ pricelist, rồi bấm **Tải PDF / IES / LDT**. PDF, IES và LDT được xử lý độc lập cho từng mã: thiếu URL/nguồn PDF chỉ bỏ qua PDF, thiếu IES chỉ bỏ qua IES và LDT, còn mọi asset có nguồn vẫn được tải/xử lý và batch vẫn tiếp tục mã sau. Với IES hợp lệ (dù vừa tải hay manifest đã xác minh), Downloader tự động chuyển IES → LDT ngay bằng canonical product name; PDF catalogue là metadata tùy chọn cho IES relative nên converter dùng fallback IES kèm cảnh báo khi PDF không có. IES absolute vẫn yêu cầu catalogue có technical flux dương; lỗi conversion đó chỉ áp dụng cho LDT, không làm mất PDF/IES đã tải. Nút **Đổi IES → LDT theo mã** vẫn được giữ để backfill dữ liệu đã có. Existing canonical/unique LDT được báo `conversion_exists` và không bao giờ bị ghi đè, kể cả khi bật `Force`; lỗi download, IES hoặc conversion được ghi theo từng asset, giữ nguyên file hợp lệ và batch tiếp tục item sau. Nút **Xuất file LDT** cho chọn một thư mục cha rồi tạo thư mục con đúng tên `File đèn` và sao chép các LDT tương ứng với toàn bộ mã đang nhập. Nút **Xuất file PDF** làm tương tự cho catalogue PDF, tạo thư mục con đúng tên `Catalouge đèn`. Cả hai luồng giữ trạng thái riêng cho từng mã (kể cả mã trùng), chỉ sao chép nguồn trùng một lần, bỏ qua đích cùng SHA-256 và báo lỗi collision cùng tên khác nội dung mà không bao giờ ghi đè. Lookup dùng đúng product/Family từ pricelist; LDT ưu tiên LDT canonical do manifest sở hữu rồi mới dùng canonical/legacy identity duy nhất, còn PDF dùng cùng resolver an toàn của chức năng mở PDF: ưu tiên `PdfPath` canonical trong manifest, rồi PDF canonical theo tên verified/product, sau đó legacy `SKU - product.pdf` duy nhất hoặc tên cũ không có SKU duy nhất. Cả lookup và export fail-closed nếu Product Family, PDF/LDT nguồn, thư mục cha đích hoặc thư mục con export đi qua symbolic link, junction hay reparse point. Không khớp substring hoặc chọn nhầm variant `840`/`865`; manifest ngoài root/sai family, tên biến thể mơ hồ, IES/LDT và khớp substring đều bị từ chối. Có thể kiểm tra tệp đã có, mở product-family tương ứng và **Mở PDF theo mã**. Nút mở PDF chỉ xử lý mã đầu tiên theo thứ tự nhập (ghi log các mã còn lại).

Pricelist cần các header `Product Family` và `Material description`; `Material` là tùy chọn. Tệp được bố trí dưới `Product Family/<family>`, với IES ở `IES` và LDT ở `LDT`; tên thư mục/tệp được sanitize cho Windows. Với asset mới, PDF/IES/LDT dùng **cùng canonical basename chỉ từ tên sản phẩm đã xác minh** (ưu tiên Signify matched name, fallback Material description), không còn tiền tố SKU: `<product>.pdf`, `IES/<product>.ies`, `LDT/<product>.ldt`. CSV `download_log.csv` giữ nguyên schema 16 cột; audit conversion điền product/family/material cùng `output_file` (PDF), `ies_file` và `ldt_file`.

`ProductAssetName` thay control, Unicode category `Format` (ví dụ zero-width joiner) và ký tự cấm của Windows bằng khoảng trắng, rồi gộp mọi run invalid/whitespace thành một khoảng trắng. Policy cũng luôn bỏ tiền tố tùy chọn `SKU - ` gồm 3–18 chữ số khi tạo canonical basename, bảo vệ tên thiết bị reserved, bỏ dấu chấm/khoảng trắng cuối và giới hạn basename ở 120 ký tự. Ví dụ `911401504347 - WT198C LED40S/840 PSU L1200` trở thành `WT198C LED40S 840 PSU L1200`. Mỗi Product Family root có manifest JSON nguyên tử `.docmanager-canonical-assets.json`, được bảo vệ bằng named mutex liên tiến trình, ghi Material, matched SKU/name, family, canonical basename và đường dẫn PDF/IES/LDT. Nếu hai SKU/logical product khác nhau trong cùng family cùng rơi vào một basename, batch dừng item sau với status `name_collision`; alias có cùng matched SKU có thể dùng lại identity. Canonical file có sẵn nhưng chưa có manifest chứng minh identity cũng bị từ chối ghi đè. Lookup/check/open/conversion ưu tiên manifest, vì vậy asset vẫn được tìm thấy khi API matched name khác với Material description trong pricelist; conversion cập nhật đường dẫn LDT trong manifest. Khi chưa có manifest, asset legacy `SKU - product` chỉ là fallback theo đúng Material số duy nhất. CSV `download_log.csv` vẫn giữ đúng 16 cột; trạng thái chi tiết PDF/IES/LDT chỉ dùng nội bộ để log UI và tổng kết. Log UI ghi riêng từng asset là đã tải/tạo, đã dùng lại, bỏ qua vì không có nguồn, bỏ qua theo yêu cầu/phụ thuộc hoặc lỗi; tổng kết cũng đếm riêng các nhóm đó. Thiếu nguồn là `Unavailable`/`Skipped`, không được đếm là lỗi; lỗi HTTP/validation/I/O/conversion là `Failed` đúng asset liên quan.

### LDT Editor

Mở hoặc kéo/thả LDT vào toàn bộ tab **LDT Editor** (nền, raw hoặc grid), chỉnh raw text hoặc grid các trường, đồng bộ grid sang raw, nạp lại, lưu, lưu thành tệp mới và mở thư mục chứa tệp. Editor bảo toàn kiểu newline và thử giải mã UTF-8, Windows-1252 rồi Latin-1. Dirty state được theo dõi cho cả raw/grid/load/save/reload; trước khi thay tài liệu LDT có thay đổi chưa lưu, editor luôn hỏi xác nhận. Khi đang preview IES thì raw/grid chỉ đọc, không dirty và đóng/mở LDT khác không hỏi lưu.

Nút **Mở IES** và toàn bộ tab **LDT Editor** nhận đúng một `.ies` (có thể kèm đúng một `.pdf`) nhưng **chỉ parse và hiển thị preview trước**, không convert hoặc ghi LDT. Raw bên trái giữ nguyên text đã giải mã và mọi CRLF/LF/CR hỗn hợp/final newline; grid bên phải tóm tắt nguồn, encoding/BOM, header LM-63, 13 trường số, relative/absolute, kích thước mm, dải/bước/mẫu góc và thống kê candela/đỉnh. PDF thả cùng chỉ được ghi nhớ làm catalogue candidate. Nút **Chuyển sang LDT...** mới bắt đầu quyết định catalogue và Save As; Save/Save As/đồng bộ bị tắt trong IES Preview, Nạp lại đọc lại IES nguồn và Mở thư mục mở thư mục IES.

Khi người dùng bấm **Chuyển sang LDT...**, editor dùng đúng snapshot/document đã preview, không đọc/parse lại IES. Lúc đó editor mới auto-discover PDF cùng thư mục hoặc family cha khi nguồn nằm trong `IES`: ưu tiên basename canonical chính xác, rồi legacy `SKU - product` chính xác và duy nhất; không fuzzy hoặc quét sibling family. Nếu không có exact PDF, dialog **Chọn catalogue PDF?** cho phép chọn tường minh hoặc tiếp tục không PDF với IES relative; IES absolute luôn yêu cầu PDF có technical flux dương. Hộp lưu mặc định `<family>/LDT/<canonical>.ldt` khi nguồn dưới `IES`, nếu không thì dùng thư mục nguồn. Đường dẫn Save As người dùng chọn được giữ nguyên; tên tệp khác canonical chỉ sinh cảnh báo, không thêm `_auto`. Ghi đè phải xác nhận và tạo `<target>.bak`. Toàn bộ PDF/convert chạy nền và hoàn tất trước khi target bị thay đổi; chỉ sau khi lưu thành công, LDT kết quả mới được nạp vào raw/grid/path/status. Nếu conversion lỗi, preview IES vẫn nguyên vẹn. Cảnh báo no-PDF, technical fallback, chuẩn hóa tên, tên đích khác canonical và overwrite được hiển thị mà không chèn vào raw LDT.

### CAD Symbol

Sau mỗi lần xuất workbook mới hoặc append form, ứng dụng quét đúng cột **Proposed calculation model** theo form đã nhận diện, khử trùng lặp bằng `v1|TextNormalization.ProductKey(...)` nhưng vẫn giữ các discriminator số như `840`/`865`, rồi hiển thị model, trạng thái mapping và số dòng sử dụng trong tab **CAD Symbol**. Nếu còn model thiếu ảnh, tab tự được chọn; nếu đã đủ mapping thì app giữ tab hiện tại và tự điền các symbol đã lưu khi tùy chọn **Tự cập nhật file hiện tại** đang bật. Dòng target ghi rõ **File vừa xuất** và đường dẫn batch đang hoạt động.

Để làm việc với workbook `.xlsx` đã có từ trước, bấm **Chọn file Excel có sẵn** và chọn đúng một file. Ứng dụng chỉ đọc/quét, yêu cầu cả hai header chính xác **Proposed calculation model** và **Symbol in DWG**, rồi thay target hiện tại bằng riêng file này; target được ghi rõ **File đã chọn** cùng đường dẫn rút gọn có tooltip đầy đủ. Thao tác chọn không áp dụng mapping và không sửa dù chỉ một byte của workbook. Chọn lại cùng file sẽ quét mới model/số dòng và trạng thái mapping. Nếu thiếu header, file tạm/lock, file đang bị Excel khóa hoặc không đọc được, ứng dụng báo lý do cụ thể và giữ nguyên danh sách/target trước đó. Sau lần xuất mới kế tiếp, target lại trở thành batch vừa xuất; hai nguồn không bị trộn ngầm.

Người dùng chọn model rồi **Dán ảnh** hoặc **Chọn ảnh** (PNG/JPG/JPEG/BMP/GIF/TIFF). Clipboard ưu tiên đúng một file ảnh trước, nếu không mới nhận bitmap; ảnh được decode frame đầu, kiểm tra giới hạn byte/kích thước/pixel và mã hóa lại PNG canonical có alpha. **Xóa ảnh** bỏ mapping và chỉ xóa drawing CAD do DocManager quản lý khỏi target hiện tại; ảnh manual và ảnh `Reference image` không bị đụng tới. Khi **Tự cập nhật file hiện tại** bật, dán/chọn/xóa ảnh mới cập nhật target; riêng thao tác chọn workbook luôn chỉ quét. Nút **Cập nhật file Excel** áp dụng lại toàn bộ mapping cho target hiện tại. Target workbook chỉ giữ trong memory và không được khôi phục qua restart để tránh tự sửa file cũ; ứng dụng chỉ nhớ thư mục CAD Excel gần nhất cho dialog lần sau.

Mapping được lưu theo user tại `%LOCALAPPDATA%\DocManager\cad-symbols`: `manifest.json` schema v1 và `images\<sha256>.png`. Manifest dùng đường dẫn tương đối, ảnh được dedupe theo SHA-256, cả manifest/ảnh đều ghi qua file tạm và thay nguyên tử, có named mutex giữa nhiều instance, kiểm tra path containment và fallback có diagnostic khi manifest hỏng. Có thể chuyển mapping sang máy/user khác bằng cách đóng app rồi copy nguyên thư mục `cad-symbols`; build/publish portable không copy repository này.

Updater ClosedXML nhận diện cột model/symbol ở schedule chuẩn hoặc form custom. Mỗi model chỉ khớp theo normalized key chính xác; ảnh được giữ tỷ lệ, căn giữa với padding và vừa trong cell. Drawing do service quản lý có prefix `docmanager-cad-symbol-v1-`, nên chạy lại là idempotent và không xóa ảnh manual/reference. Symbol cell merge chỉ được neo một lần ở ô top-left; nếu cùng anchor chứa nhiều model, updater báo conflict và không sửa drawing. Workbook được ghi ra tệp tạm rồi atomic replace; file đang khóa làm cập nhật thất bại nhưng giữ nguyên bản gốc.

## Vị trí cửa sổ

Cửa sổ chính có kích thước cố định **970 × 690 DIP**; không thể resize hoặc maximize, nhưng vẫn có thể minimize và đóng. Ứng dụng chỉ nhớ vị trí `Left`/`Top` khi đóng vào `%LOCALAPPDATA%\DocManager\window-state.json`; khi mở lại luôn ở trạng thái Normal với kích thước cố định. Dữ liệu kích thước hoặc trạng thái cũ bị bỏ qua, còn vị trí hợp lệ được phục hồi; vị trí không còn thuộc màn hình hiện có, JSON lỗi hoặc không đọc được sẽ im lặng dùng vị trí mặc định giữa màn hình.

## Cấu hình và root mặc định

Các đường dẫn người dùng chọn được lưu theo từng tài khoản Windows tại `%LOCALAPPDATA%\DocManager\user-settings.json`, bằng thao tác thay thế tệp nguyên tử. JSON thiếu, lỗi hoặc không đọc/ghi được sẽ im lặng dùng fallback an toàn; tệp này không nằm trong thư mục Released nên có thể copy bản build sang vị trí hoặc máy khác mà không mang theo absolute path cũ.

- Root catalogue/Product Family mặc định portable là `App\Product Family`, dù app .NET thật chạy từ `App\Released`. Core gọi `PortableAppPaths.ResolveRoot(AppContext.BaseDirectory)`: chỉ nâng từ leaf `Released` lên parent khi parent có marker layout (`Product Family`, `Product excel file`, launcher root hoặc publish manifest); một thư mục tình cờ tên `Released` nhưng không có marker vẫn dùng chính nó. Cả ô **Catalogue** của DiaLux và **Product Family** của Downloader dùng chung một setting và luôn đồng bộ. Nếu root đã lưu không còn tồn tại sau khi chuyển máy, ứng dụng hiển thị root portable làm vị trí dự kiến, dù thư mục đó chưa được tạo.
- Pricelist ưu tiên tệp `.xlsx` hợp lệ đã lưu. Nếu không còn hợp lệ, ứng dụng kiểm tra `App\Product excel file` trước, sau đó portable root và trong thư mục cha của Product Family; tại các vị trí đó ứng dụng tìm tên chính xác `Prof Pricelist V1.0 2026 effective_Mar2026.xlsx`, rồi mới tìm `Prof Pricelist*.xlsx`. Vì vậy layout nhóm mới và pricelist direct-root cũ đều được hỗ trợ, còn workbook không liên quan không được tự chọn. Không có fallback production hardcode tới ổ `D:`.
- Ứng dụng nhớ form Excel hợp lệ, thư mục PDF DiaLux gần nhất, thư mục output Excel gần nhất, thư mục CAD Excel gần nhất và các thư mục LDT/IES/catalogue PDF của editor. Output/CAD Excel chỉ lưu **thư mục gần nhất** để khởi tạo dialog; tệp output hoặc CAD workbook cũ không tự trở thành target đang hoạt động và không bị tự ghi đè ở lần mở sau.
- Giá trị nhập tay trong các ô đường dẫn được chuẩn hóa/lưu khi ô mất focus và khi đóng cửa sổ; tệp không còn tồn tại như pricelist/form Excel được xóa khỏi trạng thái khôi phục. Danh sách PDF input hiện tại và raw LDT document không được lưu.
- Khi chưa có catalogue hợp lệ, xuất DiaLux vẫn tiếp tục bằng dữ liệu report; riêng kiểm tra mã thiếu yêu cầu catalogue hợp lệ. Việc khôi phục UI không nới lỏng các kiểm tra root/manifest khi mở PDF catalogue.

## Quy tắc dữ liệu quan trọng

### CCT nghiêm ngặt

CCT chỉ được nhận từ một dòng có nhãn kỹ thuật CCT/correlated colour temperature/colour temperature/nhiệt độ màu, không có nhãn marketing (như `available`, `options`, `range`, `choice`, `selectable`), và có **đúng một** giá trị Kelvin hợp lệ. Giá trị được chuẩn hóa từ dấu chấm/dấu phẩy ngăn hàng nghìn và phải nằm trong 1000–50000 K. Vì vậy dòng marketing liệt kê nhiều CCT không bị chọn nhầm.

### Khớp catalogue và brochure đa-SKU

Catalogue được lập chỉ mục theo document thay vì coi tên mỗi PDF là một SKU tuyệt đối. Tệp byte-identical được khử trùng lặp bằng SHA-256. Với datasheet một SKU, filename/model tiếp tục là identity và các mã variant như `830`/`840`/`865` vẫn phải tương thích. Với brochure family có bảng `12NC`/SKU và `Product Description`, từng hàng được đọc theo cùng visual row để tạo descriptor gồm 12NC, model và token variant. Những token thực sự thay đổi trong cohort cùng model (ví dụ `LED20`/`LED40`, `CW`/`NW`, `L600`/`L1200`) trở thành discriminator động: query có discriminator không được lấy technical fact từ sibling khác; hậu tố mới chỉ có ở query như `X` không tự làm hỏng match đúng.

Technical value trong brochure đa-SKU chỉ được gắn cho variant khi layout của nguồn nối được descriptor với đúng row/column hoặc khi giá trị giống nhau một cách rõ ràng cho mọi cột family liên quan. Nếu exact variant vắng mặt/không phân biệt được, index chỉ trả ảnh và fact family-common an toàn; power/flux/CCT mang tính sibling bị bỏ. Một ngoại lệ có kiểm soát dành riêng cho descriptor của bảng sản phẩm đa-SKU: `WW` → 3000 K, `NW` → 4000 K và `CW` → 6500 K chỉ được map khi ô kỹ thuật có nhãn `Color & CCT`/`Colour & CCT` của đúng cột family chứa chính giá trị Kelvin đó trong danh sách candidate. Provenance được ghi là derived-from-variant-code + source-supported-candidate. Thiếu nhãn/list, candidate không chứa giá trị, query không map được descriptor, hoặc datasheet không phải family đa-SKU thì CCT vẫn để trống; Excel không fallback từ tên/filename. Các mã `830`/`840`/`865` ở datasheet một SKU vẫn chỉ dùng để khớp variant, không tự tạo CCT nếu không có dòng kỹ thuật CCT nghiêm ngặt.

Kích thước trong matrix family cũng phải đến từ ô có nhãn kỹ thuật `Dimension`/`Dimension. (mm)` của đúng cột. Khi ô nguồn liệt kê các chiều dài thay thế cùng width/height, token descriptor `L1200`/`L600` chỉ chọn alternative có chiều dài tương ứng; không lấy các giá trị A/B của trang hướng dẫn lắp đặt. Nhiệt độ nguồn PDF bị control glyph hoặc dấu degree dạng combining được làm sạch trước khi chuẩn hóa, nên output chỉ còn giá trị như `25°C`, không còn `Ta`/`Tq`, label hay ký tự điều khiển.

### Parameter catalogue

Cột **Parameter** chỉ thêm dòng khi catalogue có đúng thông số kỹ thuật tương ứng, theo thứ tự: `CCT`, `CRI`, điện áp, `Beam`, nhiệt độ môi trường, `Size`. CCT luôn là `CCT: <giá trị>` và chỉ đến từ evidence kỹ thuật đã map; CRI không có toán tử được hiển thị tối thiểu `CRI: >80`, còn `>`/`≥` có trong nguồn được giữ lại. Điện áp và nhiệt độ môi trường chỉ hiện giá trị, không lặp label. Beam chỉ nhận từ field kỹ thuật có nhãn `Beam angle`, `Beam angle of light source`, `Optical beam angle`, `Góc chiếu` hoặc `Góc chùm tia` với đơn vị góc; xuất thành `Beam: <góc> độ`. Kích thước hiện `Size: <giá trị>`; kích thước không nhãn có nhiều chiều được chuẩn hóa mỗi chiều với `mm` và dấu phân cách ` x ` (ví dụ `12x6x5000mm` thành `12mm x 6mm x 5000mm`), còn dạng có nhãn được giữ ngữ nghĩa như `D 170 mm x H 35 mm`; IP/IK vẫn thuộc cột **IP / IK** riêng.

### Khớp và tải Signify

Downloader thử PDF trực tiếp theo SKU trước, rồi tìm qua API. Chỉ kết quả **exact** mới được dùng theo mặc định: khớp Material/SKU/product code, hoặc khớp chính xác material description với name/displayed description. Fuzzy chỉ được xét khi người dùng bật rõ ràng checkbox **Cho phép fuzzy**; khi đó vẫn phải qua kiểm tra token/model gần biến thể, không chấp nhận ứng viên không liên quan.

Yêu cầu HTTP được retry cho 429 và lỗi 5xx; có delay theo item, timeout và cancellation. Tải xuống dùng tệp tạm cạnh đích rồi thay thế nguyên tử sau khi xác thực. PDF phải có chữ ký PDF; IES phải qua kiểm tra LM-63 tối thiểu, vì vậy nội dung HTML/XML/JSON hoặc tệp có sẵn không hợp lệ sẽ không bị báo thành công. IES fallback lần lượt dùng URL API, configurator theo SKU và slug theo mô tả.

### IES sang Eulumdat

Parser chỉ nhận LM-63 có `TILT=NONE` và photometric **Type C (1)**. Type A/B bị từ chối vì cần coordinate transform trước khi có thể ghi Eulumdat. Ma trận candela được kiểm tra theo count/góc/đơn vị, nhân candela multiplier, giới hạn tích số mẫu ngang × đứng ở 10 triệu trước khi cấp phát/đọc ma trận, và kích thước feet/metre được đổi sang mm.

Converter hỗ trợ dữ liệu Type C với Gamma `0..180`, hoặc nguồn `0..90` có bước đều chia khít đến 180; trường hợp `0..90` được mở rộng phần uplight bằng cường độ 0. C-plane được nội suy và mở rộng theo đối xứng phù hợp; output giữ cường độ cd/klm. IES absolute (`lumens per lamp = -1`) chỉ được chuyển khi catalogue có **technical luminaire flux** dương; flux này bắt buộc cho chuẩn hóa Eulumdat. Không có flux hợp lệ thì conversion dừng với lỗi thay vì ghi LDT sai.

Batch conversion nhận product context từ manifest trước, rồi mới fallback pricelist/legacy: canonical product name và đúng một PDF/IES đã resolve trong chính family. PDF catalogue chỉ được chọn khi basename canonical khớp chính xác, hoặc legacy `SKU - product` khớp chính xác và duy nhất; token overlap/fuzzy không được dùng cho technical override. LDT luôn được tạo đúng `LDT/<canonical product>.ldt`, kể cả Unicode; dòng 9 và 11 là canonical product name, dòng 10 để trống. Sau conversion, đường dẫn LDT được ghi nguyên tử vào manifest. Existing canonical LDT được skip. Legacy LDT còn discover được bằng product identity/header duy nhất và được skip bảo thủ; ứng dụng không bulk rename asset cũ và không dùng `_auto` cho output mới.

Catalogue PDF chỉ override technical field khi có label/evidence hợp lệ: luminaire flux, power, CRI, CCT và **kích thước physical overall**. Geometry dùng model riêng có shape `Circular`/`Rectangular`, length-or-diameter, width, height theo mm và evidence page/label/raw. Chỉ nhận các nhãn overall tường minh: `Overall diameter`/`Đường kính tổng thể` kèm `Overall height`/`Chiều cao tổng thể`, ba nhãn overall length/width/height đầy đủ, hoặc một dòng `Overall dimensions`/`Kích thước tổng thể` L×W×H đầy đủ. Giá trị phải dương, hữu hạn, có `mm` và trong giới hạn bảo thủ; cut-out/opening/mounting/installation/drawing/packaging/carton/shipping/quantity bị từ chối. Không suy luận từ token tên sản phẩm như `D100`, `W30L120` hoặc `L5000`; evidence thiếu hoặc xung đột thì fallback IES và sinh warning.

Provenance được giữ trong conversion result và tóm tắt ở dòng 12. Manufacturer và lamp luôn lấy từ IES. Khi catalogue có overall geometry đầy đủ, LDT physical lines 13–15 dùng catalogue: circular là diameter, 0, height; rectangular là length, width, height. Nếu không đủ evidence, physical geometry fallback IES và vẫn đổi đúng feet/metre sang mm. Theo mapping hiển thị trong LDT Editor, luminous-area lines 16–18 mặc định phản chiếu chính các kích thước physical đã resolve ở lines 13–15: length/diameter, width (vẫn là 0 với circular) và height C0. Giá trị luminous-area tường minh hợp lệ cho từng trường 16–18 vẫn được ưu tiên; trường thiếu/0 fallback về physical tương ứng. Lines 19–21 chỉ giữ các height C90/C180/C270 tường minh nếu có, nếu không vẫn bằng 0 và không tự lặp height C0. Catalogue identity/name/SKU không được ghi đè canonical product context. `IesToLdtSaveAsService` trong `DocManager.Photometry` cung cấp luồng headless/testable cho editor: parse IES đúng một lần, trích PDF tường minh nếu có, convert bằng canonical product name, lưu nguyên tử đúng destination đã chọn, fail-closed khi target tồn tại mà không cho overwrite, tạo `.bak` khi overwrite, rồi trả `LdtDocument` đã nạp cùng conversion result và typed warnings.

## Bảo vệ dữ liệu và backup

- LDT Editor lưu qua tệp `.part`. Khi ghi đè, `File.Replace` thay target và chụp nội dung cũ; bản cũ được đưa thành `<tệp>.bak`. Nếu không thể promote backup, target được phục hồi từ bản đã chụp và `.bak` cũ không bị thay trước khi thao tác thành công.
- Lưu thành tệp mới không tạo backup vì không ghi đè target cũ.
- Tải catalogue/IES cũng dùng tệp tạm và chỉ thay đích sau xác thực hoàn tất.
- Nếu `download_log.csv` hiện hữu có header/schema khác, nó được đổi tên thành `<tệp>.<yyyyMMddHHmmss>.legacy.bak` trước khi tạo log đúng schema; không append lệch cột vào log cũ. Dòng audit `converted` chứa product/family/material, canonical identity, PDF catalogue chính xác, source IES và output LDT để truy vết đầy đủ.

## C# packages và giấy phép

Các package NuGet trực tiếp hiện dùng:

- [ClosedXML 0.105.0](https://github.com/ClosedXML/ClosedXML) — MIT; đọc/ghi XLSX.
- [PdfPig 0.1.12](https://github.com/UglyToad/PdfPig) — Apache-2.0; trích text/positioned words, đọc annotation trang 1 và enumerate ảnh raster cùng bounding box hiển thị.
- WPF `System.Windows.Media.Imaging` của .NET 8 Windows — thành phần framework Microsoft, dùng crop/scale/encode PNG trong memory; không có runtime/package trả phí, không có native PDF runtime riêng và không dùng `System.Drawing.Common`. Vì pipeline này phụ thuộc WPF imaging, `DocManager.Dialux` và test target `net8.0-windows`; app desktop vốn đã target `net8.0-windows`.
- [Microsoft.NET.Test.Sdk 17.14.1](https://github.com/microsoft/vstest) — MIT.
- [xUnit 2.9.3](https://xunit.net/) và [xunit.runner.visualstudio 3.1.1](https://github.com/xunit/visualstudio.xunit) — Apache-2.0.
- [coverlet.collector 6.0.4](https://github.com/coverlet-coverage/coverlet) — MIT.

Không có package thương mại hoặc package yêu cầu runtime license, và không có dependency Python.

## Khoảng trống deferred còn lại

Đây không phải tuyên bố full parity. Các phần chưa triển khai thực sự là:

1. phục hồi đầy đủ kích thước technical drawing/CID;
2. RoomBook và tính toán lux;
3. specialized BOH summary/append;
4. OCR cho nội dung PDF không trích xuất được text;
5. external normative validation/chứng nhận Eulumdat theo tiêu chuẩn bên ngoài.

Giới hạn ảnh còn lại: extractor chỉ xử lý ảnh raster mà PdfPig enumerate và decode được trên trang 1. PDF chỉ có vector hero, ảnh ghép từ nhiều layer/mask, nền không gần trắng/trong suốt mà foreground không thể tách tin cậy, hoặc không có ứng viên an toàn sẽ giữ nguyên raster đã chọn hay để trống `Reference image`; ứng dụng chủ ý không fallback sang screenshot toàn trang. Annotation chỉ tăng độ chính xác khi chọn object; PDF không có annotation vẫn dùng heuristic an toàn tổng quát.
