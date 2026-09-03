# DocManager

Ứng dụng desktop Windows viết bằng C#/.NET 8 cho bốn luồng công việc: **DiaLux → Excel**, **Catalogue Downloader** (Signify, kèm IES → LDT), **LDT Editor** và **CAD Symbol**. Ứng dụng native .NET không phụ thuộc Python hay script bên ngoài.

<!-- ![DocManager screenshot](docs/screenshot.png) -->

## Tính năng chính

**DiaLux → Excel**
- Chọn hoặc kéo/thả nhiều PDF báo cáo DiaLux.
- Xuất từng workbook hoặc gộp chung, mở rộng dữ liệu từ catalogue PDF.
- Hỗ trợ append vào form Excel hiện hữu theo header nhận diện.
- Merge ô phòng, công thức tổng/hiệu suất, hình ảnh sản phẩm được tự động cắt và nhúng.

**CAD Symbol**
- Kho thư viện CAD block phân loại theo mô hình tính toán dự kiến.
- Cập nhật "Symbol in DWG" trong các workbook Excel chỉ định.

**Catalogue Downloader**
- Nạp pricelist XLSX, nhập mã sản phẩm (một mã mỗi dòng hoặc cách bằng dấu phẩy).
- Tải song song bốn luồng: PDF catalogue, IES photometry, chuyển IES → LDT.
- Xuất PDF/LDT vào thư mục con, giữ file đã có, báo lỗi chi tiết từng mục.
- Bảng kết quả dạng drawer trượt từ mép phải, double-click mở thư mục.

**LDT Editor**
- Mở/edit single .ldt file hoặc preview read-only .ies.
- Chuyển IES → LDT ngay trong ứng dụng.
- Đồng bộ giữa văn bản thô và lưới có cấu trúc.

**Tiện ích chung**
- Thu nhỏ xuống khay hệ thống. Bấm Minimize thì cửa sổ ẩn xuống khay góc phải taskbar. Menu chuột phải trên icon khay có "Mở cửa sổ" và "Thoát". Nút X vẫn thoát bình thường.
- Nhớ giữa các phiên: danh sách mã đang làm dở, ô gộp báo cáo, ô tự cập nhật CAD Symbol, ô tải IES. Đóng app hay mất điện không mất danh sách mã.
- Hai tùy chọn trong Catalogue Downloader: "Tải file IES" (bật sẵn, có nhớ) và "Tải lại, ghi đè file đã có" (tắt sẵn, cố ý không nhớ giữa các phiên để tránh vô tình tải đè toàn bộ catalogue).
- Gợi ý kéo thả hiển thị ngay trong giao diện ở danh sách PDF và tab LDT Editor.

## Tải bản chạy sẵn

Bản portable đóng gói sẵn nằm ở mục [Releases](https://github.com/Thebl3/DocManager/releases). Giải nén rồi chạy `DocManager.Desktop.exe`, không cần cài .NET SDK.

Hai thư mục `Product Family` và `Product excel file` trong gói để trống. Đặt catalogue và file pricelist của bạn vào đó trước khi dùng tab Catalogue Downloader.

## Yêu cầu

- Windows 10 hoặc Windows 11.
- .NET 8 SDK phiên bản 8.0.423 trở lên.

**Thư viện bên thứ ba:**
- ClosedXML 0.105.0
- DocumentFormat.OpenXml 3.3.0
- PdfPig 0.1.12

Ứng dụng sử dụng `System.Net.Http.HttpClient` built-in cho HTTP; không có phụ thuộc thêm cho web scraping hay browser automation.

## Build và chạy

Tất cả lệnh dùng cấu hình Debug:

```powershell
dotnet restore DocManager.sln
dotnet build DocManager.sln -c Debug --no-restore
dotnet run --project src/DocManager.Desktop/DocManager.Desktop.csproj -c Debug --no-build
```

Chạy test:

```powershell
dotnet test DocManager.sln -c Debug --no-build
```

## Cấu trúc thư mục

```
src/
  DocManager.Core/               Shared models, text/number normalization, CCT
  DocManager.Dialux/             Parse DiaLux PDF, Lighting Schedule export
  DocManager.Signify/            Pricelist, Signify catalogue, IES/LDT layout
  DocManager.Photometry/         IES LM-63 parser, IES to Eulumdat converter
  DocManager.Desktop/            WPF UI, async task orchestration
tests/
  DocManager.Tests/              xUnit tests, 420 passed, 12 skipped
tools/
  DocManager.Launcher/           .NET self-contained single-file launcher
```

## Dữ liệu và cấu hình

Canonical output layout dưới Product Family root:

```
{ProductFamily}/
  {ProductName}.pdf
  IES/{ProductName}.ies
  LDT/{ProductName}.ldt
download_log.csv
```

Settings lưu trong `%LocalAppData%\DocManager\`:

```
user-settings.json
window-state.json
cad-symbols/
```

## Kiểm thử

Chạy `dotnet test` để chạy xUnit suite. Hiện tại: **420 passed, 12 skipped (432 total)**.

## Tài liệu chi tiết

Xem [`README.vi.md`](README.vi.md) để tìm hiểu sâu hơn: build script, quy trình publish, kiến trúc nội bộ, trình tự khởi động.

## Giấy phép / Lưu ý

Các file PDF catalogue và IES photometry dưới thư mục `Product Family/` là tài sản của Signify/Philips. Chúng được bao gồm trong repository này chỉ như dữ liệu làm việc, không phải quyền phân phối lại.
