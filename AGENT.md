# AGENT.md — BIM-DatViet

## 1. Mục đích file

Đây là ghi nhớ làm việc của project `BIM-DatViet`. Khi bắt đầu phiên mới, đọc file này trước để nắm mục tiêu, quy tắc trao đổi, kiến trúc và trạng thái gần nhất. Cập nhật lại file khi có thay đổi quan trọng về quyết định thiết kế, capability hoặc trạng thái chạy thực tế.

## 2. Cách làm việc với anh Mèo Cọc

- Giao tiếp 100% bằng tiếng Việt; AI xưng em, gọi người dùng là anh.
- Trả lời ngắn gọn, dễ hiểu trước; chỉ đi sâu khi anh yêu cầu.
- Phân biệt rõ ý định:
  - Anh hỏi “là gì”, “tại sao”, “làm sao”, “chỉ anh” hoặc đang thảo luận: chỉ giải thích, không tự sửa code hay thao tác Revit.
  - Anh yêu cầu “làm”, “sửa”, “xóa”, “triển khai”: được phép thực hiện và phải kiểm tra kết quả.
- **Quy trình 4 bước bắt buộc cho mọi thao tác/nhiệm vụ**:
  1. **Nghiên cứu kỹ codebase trước khi làm (Pre-flight Research)**: Luôn kiểm tra kỹ các file liên quan, contract API, schema và logic hiện hữu; tuyệt đối không đoán mò cấu trúc hay đường dẫn.
  2. **Triển khai chính xác**: Chỉ sửa đúng phạm vi được yêu cầu, bảo toàn comment/docstring không liên quan.
  3. **Kiểm nghiệm & Re-check sau khi làm (Post-flight Verification)**: Sau mỗi thay đổi, bắt buộc phải kiểm tra lại (chạy unit test `dotnet test`, build check, kiểm tra syntax/log) để đảm bảo độ chính xác 100%.
  4. **Snapshot & Báo cáo bằng chứng**: Xác nhận kết quả thực tế bằng log/test/code diff rõ ràng trước khi tuyên bố hoàn thành.
- Nếu thông tin hình học hoặc yêu cầu kỹ thuật còn mâu thuẫn, phải trao đổi với anh trước; không tự bịa hoặc tự chọn giả thiết có ảnh hưởng thiết kế.
- Trước khi đóng Revit, luôn kiểm tra `Document.IsModified`. Nếu model chưa lưu, hỏi anh chọn lưu, bỏ thay đổi hay giữ Revit đang mở.
- Không tự động thêm phạm vi ngoài yêu cầu. Không để lại command, class hoặc code thừa sau khi đã thống nhất xóa hoàn toàn.

## 3. Tổng quan solution

Thư mục làm việc:

```text
C:\Users\ADMIN\Downloads\01. Cong Viec DV\Revit API\Phát Triển REVIT API
```

Solution chính gồm hai project:

```text
BIM-DatViet\                 Add-in chạy trong Revit
BIM-DatViet.Tests\           Unit test cho thuật toán thuần
```

`BIM-DatViet.csproj` là project SDK-style. Các file `.cs` trong thư mục `BIM-DatViet` và các thư mục con được tự động đưa vào compile, trừ `bin`, `obj` và các file được khai báo `Compile Remove`.

Build mặc định phải giữ `DeployAddin=false` và `LaunchRevit=false`. Chỉ bật hai thuộc tính này tường minh trong một rollout đã xác nhận đúng phiên bản Revit, đúng DLL/preset và đúng model bản sao; không dùng build offline để ngầm deploy hoặc khởi động Revit.

`BIM-DatViet.Tests.csproj` là project xUnit chạy trên .NET 8; không được nạp vào Revit. Nó link các kernel và model thuần để kiểm tra thuật toán không phụ thuộc Revit runtime.

Các lớp chính:

```text
Commands/          Điểm vào từ ribbon
Controllers/       Điều phối Analyze, chọn vùng, Create và Rebuild Zone
Recognition/       Đọc geometry, thiết lập local frame, nhận diện footing và cọc
Domain/            Thuật toán hình học thuần, gồm resolver hit-test theo local zone
Planning/          Chuyển geometry và preset thành các centerline thép
RevitServices/     Tạo Rebar, metadata, readback và rollback
Infrastructure/    Đọc preset, ghi receipt và dữ liệu phụ trợ
Models/            Schema và dữ liệu trao đổi
Preview/           DirectContext preview còn dùng cho workflow hố ga
Views/             Giao diện WPF
Resources/         Icon, preset JSON và shared parameter
```

## 4. Ribbon hiện tại

Tab `ĐVB_ADDIN`, panel `Commands` hiện chỉ có:

1. `Rải Thép Hố Ga` → `StartupCommand`.
2. `Rải Thép Mố Cầu` → `AbutmentRebarCommand`.

Command `Chuẩn Hóa Type Thép Mố` và class `AbutmentRebarTypeCommand` đã được xóa hoàn toàn. Revit 2026 đã được khởi động lại và ribbon đã được kiểm tra trực tiếp chỉ còn hai command trên.

## 5. Mục tiêu command Rải Thép Mố Cầu

Mục tiêu dài hạn là rải thép mố cầu theo geometry và profile kỹ thuật, không phụ thuộc vào một ElementId hoặc một Type name cố định.

Nguyên tắc:

- Host là `Generic Model` có bật khả năng làm rebar host.
- Host có thể là một `FamilyInstance` đơn hoặc một cụm component được khóa bằng `Revit Assembly`; mỗi Rebar phải giữ đúng `HostId` của component chứa nó.
- Tool đọc solid, đỉnh, cạnh và parameter của toàn bộ component host; tạo local frame dọc–ngang–đứng; sau đó phân vùng hình học ảo bên trong cụm.
- Không nhận diện bằng tên Type đơn thuần. Family/item identity và geometry gate phải cùng đạt.
- Không đủ bằng chứng hình học thì trả về `Ambiguous` hoặc Error và chặn Create.
- Create theo cơ chế create-first, readback, thay thế thép cũ trong `TransactionGroup`; mọi rollback phải xác nhận `TransactionStatus.RolledBack`, nếu không thì báo fail-closed và không tuyên bố model nguyên trạng.
- Thép do tool quản lý phải có ownership metadata và receipt truy vết.

## 6. Capability và nguyên tắc tái sử dụng của command Rải Thép Mố Cầu

`Rải Thép Mố Cầu` phải được phát triển như một engine dùng chung cho nhiều mố cầu, không phải command đóng khung theo Cầu Vạn Củi M2. Mố M2 hiện tại chỉ là pilot để kiểm chứng thuật toán và không được trở thành geometry mặc định của hệ thống.

Kiến trúc phải tách ba lớp:

1. **Engine dùng chung:** đọc geometry Revit, thiết lập local frame, nhận diện zone, offset cover, clip đường thép, tránh cọc/chướng ngại, tạo preview, Create, readback, metadata và rollback.
2. **Profile theo mố:** khai báo dấu hiệu nhận diện, topology dự kiến, tolerance, cover, quy tắc thép, yêu cầu cọc và nguồn bản vẽ. Mỗi mố hoặc nhóm mố tương đồng có thể dùng một profile riêng.
3. **Geometry thực tế trong model:** nested profile family cung cấp ordered loop/section authority; solid host cung cấp containment, face validation và mismatch gate. Không được ép geometry thực tế về kích thước của mố pilot.

Yêu cầu bắt buộc:

- Không hard-code `Document`, `ElementId`, Family name, Type name, kích thước, góc xéo, số lượng cọc, bar mark hoặc đường dẫn hồ sơ vào create path.
- Tài liệu tham khảo pilot chỉ là metadata/evidence của profile, không phải điều kiện để command chạy.
- Kích thước preset chỉ là giá trị dự kiến kèm tolerance. Biên tạo thép phải lấy từ ordered loop của nested profile family; solid host chỉ dùng kiểm tra mismatch/containment và clip đường thép.
- Mố có cùng topology nhưng khác kích thước, góc xéo, hướng đặt, Family/Type hoặc bố trí cọc phải dùng lại được engine bằng cách thay profile, không sửa thuật toán lõi.
- Với topology mới chưa được engine hỗ trợ, ví dụ biên lõm, nhiều solid chồng lấn/mơ hồ hoặc mặt cong, command phải trả về `Ambiguous`/Error và chặn Create; không được nội suy theo mố pilot. Lỗ mở/recess chỉ được clip khi rule khai báo `ApprovedDetail` và có evidence hợp lệ.
- Một profile mới chỉ được phép Create sau khi Analyze, chọn đúng vùng, Xem trước và các geometry gate của chính mố đó đạt yêu cầu.

Phạm vi topology dùng lại hiện hỗ trợ footing có ba station section L/Center/R, loop phẳng kín và plan hình chữ nhật/hình bình hành. Không loft profile thành solid trung gian. Topology mới phải có profile/geometry strategy và fail-closed gate riêng trước khi mở capability.

## 7. Preset P0 an toàn và source matrix Cầu Vạn Củi M2

Profile pilot hiện hành:

```text
Resources/Presets/cau-van-cui-m2.v1.json
profileId: DVB-ABUTMENT-CAU-VAN-CUI-M2-V1
schemaVersion: 2.3
ruleVersion: 2026-08-11.4
ruleHash: 0A566A6EE3A44F8236DC1748508C3D9220AC66958EB28A140F9557AECD0D6AF2
```

Nguồn chính của Cầu Vạn Củi là `QUYEN 2.4.2 HO SO CAU VAN CUI KM2+700.pdf`: P38 cho bệ mố và P39 cho thân/tường đỉnh mố. `MACL-iDECO-CD-SB-DR-MO_A1 mẫu tham khảo.pdf` chỉ là tài liệu tham khảo hình học/cấu tạo, không được dùng lấy diameter, spacing, cover, shape, hook hoặc lap cho Create.

Preset P0 giữ source matrix 27 hàng nhưng `enabled=false` toàn bộ; `AbutmentActionGate` trả `NoApprovedRule`, nên Create/Rebuild bị khóa dù Analyze nhận diện được geometry. Source matrix hiện có:

| Source P38 | Canonical add-in | Trạng thái |
|---|---|---|
| F1 | A1 | mark/diameter/spacing/station pack đã ghi; thiếu cấu tạo bắt buộc |
| F2 | A2 | mark/diameter/spacing/station pack đã ghi; thiếu cấu tạo bắt buộc |
| F3 | A3-T và A3-B | tách hai rule để thể hiện hai mat; bản vẽ chưa khóa dứt điểm một U hay hai mat |
| F4 | A4 | chưa khóa đường chạy độc lập/U-bar |
| F5 | A6 | chỉ là candidate từ chuỗi đứng P38; chưa khóa role |
| F6 | F1 | candidate thanh đặc biệt; chưa khóa shape/path |
| F6A | F2 | candidate thanh đặc biệt; chưa khóa shape/path |
| F7 | F3 | candidate thanh đặc biệt; chưa khóa shape/path |

P39 giữ 18 mark `W1/W2/W2A/W2D/W3/W3A/W4/A1/A1A/A2/A2A/A2B/A2C/A3/A4/A5/W5/W6` dưới canonical `B1…B18`, trạng thái `Proposed`, disable toàn bộ. Không dùng lại canonical A/F của P38.

Mỗi rule có hai identity độc lập: `canonicalMark` dùng nội bộ add-in và `sourceDrawingMark` giữ nguyên mark trên bản vẽ; source locator ghi drawing code, page, detail, section, note và callout. Validation cấm trùng canonical trong cùng zone, cấm mark ngoài A/B/C/D/P/N/S/F, cấm canonical `D*` trong command Mố và yêu cầu locator cho mọi enabled rule.

Chỉ được mở từng mark sau khi đã khóa đồng thời: cover, centerline path và số lượng/station, layer order, RebarShape/segment signature, hook/tie/termination, lap/development/continuity, pile/obstacle policy, bar grade/StructuralAsset và nguồn phê duyệt. Không được mở lại hàng loạt F1–F4 chỉ vì wave cũ từng Create pass trên SAFE.

## 20. Hotfix runtime 0 bar + wave thân mố (2026-08-10)

### Triệu chứng anh gặp
- Analyze nhận host `MO CAU - LBH`, 5 canonical zone, F1–F4 bật nhưng **0 thanh**, Create khóa.
- Dropdown loại thép trống.
- Thân/cánh chưa có profile Create.

### Root cause Footing 0 bar
1. Model thiếu hoặc tên `RebarBarType` không khớp exact `D16/D20/D32` → `TryResolveBarType` fail-closed từng rule → 0 bars + `plan.CanCreate=false`.
2. UI không nhấn mạnh lỗi catalog rỗng; PreviewNotice bị ghi đè thành “Đề xuất 0 thanh”.

### Fix đã ship
1. `RebarTypeCatalog.TryResolve`: exact name → diameter token → measured diameter; không tự tạo type.
2. Controller `AlignPresetBarTypes` lúc open/Analyze; issue `REBAR_BAR_TYPE_CATALOG_EMPTY`.
3. ViewModel giữ forced notice khi thiếu/sai bar type.
4. Wave thân/đỉnh tường thẳng (tờ 39): enable `CVC-W1/W2/W3`, `CVC-A1/A2/A3` với ends=`None`, ExplicitStations, cover 100; W3 chuyển từ FreeForm → `HorizontalFront`.
5. FreeForm/đai/cánh vẫn khóa.

### Bằng chứng
- Unit test **99/99**.
- Build `Release.R25` 0 warning/0 error.
- Preset `ruleVersion 2026-08-10.2`.

### Việc anh cần kiểm trên SAFE
1. Model phải có `RebarBarType` D14/D16/D20/D28/D32 (hoặc type đường kính tương đương).
2. Chạy **Rải Thép Mố Cầu** → Phân tích:
   - Footing F1–F4 có số thanh > 0.
   - Dropdown zone có thêm Thân mố / Tường đỉnh khi geometry nhận diện.
3. Nếu vẫn 0 thanh: đọc dòng lỗi `REBAR_BAR_TYPE_*` trên panel issues — tạo type thiếu rồi Analyze lại.
4. Wingwall vẫn chưa Create (thiếu mark/Φ/spacing hồ sơ cánh).

## 21. Hotfix `Host is not a valid rebar host` (2026-08-10)

### Root cause đã xác nhận
- `RebarHostData.GetRebarHostData(element) != null` chỉ cho biết element có dữ liệu/potential rebar host; không đảm bảo Revit cho phép tạo reinforcement.
- Contract đúng trước `Rebar.CreateFromCurves` là `RebarHostData.IsValidHost(element) == true`.
- Selection, recognition và preflight cũ cùng dùng check `GetRebarHostData != null`, nên Analyze có thể xanh nhưng Create mới throw `ArgumentException: Host is not a valid rebar host`.

### Fix đã deploy R25
1. Migrate các host gate sang `RebarHostData.IsValidHost` trong abutment, manhole và factory chung.
2. `SingleHost` luôn dùng `plan.Context.Host`; không dùng owner id của nested solid.
3. Preflight gom lỗi theo host, chặn trước transaction và phân biệt:
   - không có `RebarHostData`: category không hỗ trợ reinforcement;
   - có `RebarHostData` nhưng `IsValidHost=false`: kiểm tra family `Can Host Rebar`, solid hợp lệ và reload vào project.
4. Factory log `ABUTMENT_CREATE_HOST_RESOLVE` và wrap lỗi `ABUTMENT_CREATE_FROM_CURVES_FAILED` với host id/name/category.

### Bằng chứng và trạng thái runtime
- Build `Release.R25`: 0 warning, 0 error.
- Unit test: 99/99.
- DLL build và DLL live trong `%AppData%\Autodesk\Revit\Addins\2025\BIM.DatViet` cùng SHA-256.
- Chưa claim Create thành công trên model: Revit hiện chuyển sang family `TƯỜNG CÁNH - ADAPTIVE.rfa`; không được đóng hoặc mutate file người dùng để ép smoke.
- Khi quay lại project, host mố phải đạt `RebarHostData.IsValidHost=true`. Nếu false, sửa đúng family mố (`Can Host Rebar`) rồi reload/overwrite; add-in không tự ý sửa family/project.

## 8. Quy ước geometry dùng chung

Engine phải làm việc trong local frame của host, không phụ thuộc trục global hoặc hướng đặt family:

- Trục dọc, trục ngang và trục đứng phải được suy ra từ transform và geometry có đủ bằng chứng.
- Kích thước thiết kế theo phương chiếu dùng để nhận diện/kiểm tra; không mặc định là chiều dài cạnh polygon.
- Biên dùng để offset cover, sinh line và clip thép phải là boundary thật đọc từ geometry, gồm hướng cạnh và chiều dài cạnh thật.
- Offset cover phải thực hiện vuông góc vào từng cạnh thật của boundary.
- Footing phải được nhận diện từ các mặt/band hình học thực tế. `footingDepthMm` trong profile là dữ liệu hỗ trợ và phải được đối chiếu với model, không mặc định luôn bằng 2.000 mm.
- Mọi điểm, vector và khoảng cách phải được xử lý nhất quán trong local frame; host được xoay, mirror hoặc đặt xéo không làm thay đổi ý nghĩa rule dọc/ngang.

Ví dụ riêng của pilot Cầu Vạn Củi M2:

- `L_MO = 15.800 mm` là chiều dài chiếu theo trục dọc thiết kế.
- Với góc xéo khoảng 25°, cạnh xiên tương ứng xấp xỉ `15.800 / cos(25°) = 17.433 mm`.
- `B_BE/B_BE1 = 6.400 mm` là kích thước theo trục ngang thiết kế.

Các giá trị trên chỉ là fixture kiểm chứng. Profile khác phải đọc giá trị của chính nó, còn đường thép cuối cùng luôn bám boundary thực tế.

## 9. Cọc và kiểm soát clearance dùng chung

Cọc/chướng ngại phải được xử lý như dữ liệu tùy chọn theo từng profile:

- Cọc có thể là các `FamilyInstance` riêng dưới footing; nhận diện bằng identity có cấu hình kết hợp quan hệ không gian và geometry gate.
- Tên family/type chỉ là tín hiệu hỗ trợ, không đủ để kết luận một element là cọc.
- Không dùng số cọc kỳ vọng để cảnh báo hoặc khóa workflow; engine chỉ xử lý các bao cọc thực tế đã nhận diện.
- Đường kính, clearance, planning margin, vùng tìm kiếm cọc, tolerance đồng tâm và chiều dài đoạn thép tối thiểu phải lấy từ profile hoặc geometry đã xác nhận.
- Thanh thép chỉ bị cắt né cọc khi bao đứng vật lý của cọc giao đúng cao độ đoạn thanh; cọc được tìm theo toàn bộ component host và bị khử trùng theo vị trí hình học.

Giá trị đang dùng để kiểm chứng pilot Cầu Vạn Củi M2:

```text
Pile clearance thiết kế:       75 mm
Search expansion mặt bằng:    500 mm
Search depth dưới footing:  5.000 mm
Tolerance đồng tâm cọc:       200 mm
Placement margin planning:     30 mm
Đoạn thép tối thiểu giữ lại:  200 mm
```

Planning dùng thêm 30 mm để hấp thụ việc Revit tự hiệu chỉnh endpoint. Readback vẫn kiểm tra nghiêm clearance thiết kế 75 mm; margin không làm giảm yêu cầu nghiệm thu. Các giá trị trên là rule của profile pilot, không phải default chung.

## 10. Trạng thái kỹ thuật gần nhất

- Engine geometry, planner, preview, factory, ownership, metadata, centerline/hook/shape readback và rollback vẫn tồn tại; preset P0 không cấp quyền Create cho bất kỳ rule nào.
- `Resources/Presets/cau-van-cui-m2.v1.json` có 27 source-matrix rows, 0 enabled; P38 có 9 row, P39 có 18 row. Analyze được phép đọc geometry và hiển thị source matrix, nhưng action gate phải dừng tại `NoApprovedRule`.
- Source schema `2.3` có `canonicalMark`, `sourceDrawingMark`, `sourceResolutionStatus`, `sourceLocator`, `shapeSignature` và metadata JSON tương ứng. Shared parameter `DVB_CanonicalMark` giữ canonical mark trên Rebar khi một rule tương lai được phê duyệt.
- `RebarApiAdapter` truyền `RebarStyle` rõ ràng. Hook runtime dùng `RebarHookType + RebarHookOrientation` trên R25/R26; R27 hiện fail-closed vì mapping `BarTerminationsData` chưa được chứng minh. `CurveDrivenFreeForm` từ chối hook.
- Shape-driven rule tương lai phải khóa `RebarShapeName`, `RebarStyle`, segment count/signature và `createNewShape=false`; factory readback đối chiếu shape id/name/style/segments. Không dùng `createNewShape=true` trong production.
- F3 được tách `A3-T/A3-B` để biểu diễn hai mat nhưng cả hai vẫn `Unresolved`; F4 và F5/F6/F6A/F7 chưa đủ path/shape/role. P39 chỉ `Proposed`.
- Topology, local frame, boundary và pile detection vẫn phải lấy từ geometry thật; không hard-code model pilot hoặc dùng PDF tham khảo để sinh đường thép.
- Unit test offline hiện tại: **115/115 pass**. Build R25/R26/R27 đều 0 warning/0 error sau restore đúng target. Offline test/build không thay thế smoke UI trên model copy với package mới đã nạp.

## 11. Kiểm tra và triển khai

Build add-in Revit 2026 mà không tự deploy hoặc tự mở Revit:

```powershell
dotnet build BIM-DatViet/BIM-DatViet.csproj -c Release.R26 --nologo -p:DeployAddin=false -p:LaunchRevit=false
```

Chạy unit test:

```powershell
dotnet test BIM-DatViet.Tests/BIM-DatViet.Tests.csproj -c Release --nologo
```

DLL live đang dùng cho review (Revit **2025**):

```text
%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet\BIM.DatViet.dll
```

Preset live R25:

```text
%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet\Resources\Presets\cau-van-cui-m2.v1.json
```

(Đường dẫn R26 tương ứng dưới `Addins\2026\...` khi build/deploy target 2026.)

Ribbon được tạo trong `Application.OnStartup()`, vì vậy thay đổi registration button chỉ xuất hiện sau khi DLL mới được nạp và Revit được khởi động lại.

## 12. Điều kiện nghiệm thu khả năng tái sử dụng

Một mố mới có cùng topology chỉ được xem là dùng lại thành công khi:

1. Có profile và nguồn evidence riêng; không sửa profile pilot để chứa nhiều dự án.
2. Không phải sửa engine chỉ vì khác kích thước, góc xéo, hướng đặt, Family/Type, số lượng hoặc đường kính cọc.
3. Analyze xác lập được local frame, boundary, zone và các geometry gate từ model mới.
4. Preview cho thấy đường thép đúng phương, đúng cover, nằm trong host và xử lý đủ chướng ngại.
5. Create, readback, ownership metadata, receipt và rollback hoạt động trên model mới.
6. Các rule chưa đủ evidence hoặc chưa có geometry strategy vẫn fail-closed.
7. Có kiểm tra trực tiếp trong Revit và test thuật toán thuần cho biến thể geometry tương ứng.

Nếu phải thêm topology mới, phần code bổ sung phải là geometry strategy dùng chung có fixture/test riêng; không thêm nhánh `if` theo tên dự án, tên Family/Type hoặc ElementId.

## 13. (dành số)

Mục 13 cố ý bỏ trống để giữ số mục lịch sử; nội dung runtime mới nằm ở §17.

## 14. Cập nhật mới nhất (Phiên làm việc 2026-08-03)

1. **Sửa lỗi thiếu lớp thép F2 (Đáy • phương ngang) ở bệ móng**:
   - Khắc phục nguyên nhân `pileObstaclePolicy` của rule `CVC-F2` trong [`cau-van-cui-m2.v1.json`](file:///c:/Users/ADMIN/Downloads/01.%20Cong%20Viec%20DV/Revit%20API/Ph%C3%A1t%20Tri%E1%BB%83n%20REVIT%20API/BIM-DatViet/Resources/Presets/cau-van-cui-m2.v1.json) bị đặt sai thành `ApprovedDetail` làm mẩu thép F2 bị cắt nhỏ rác né cọc và bị loại bỏ hết. Đã cập nhật thành `ContinueThroughMonolithicHost`.
   - Khắc phục lỗi lùi Double Cover trong `AbutmentRebarPlanner.cs:L488-L499` cho SingleHost. Khôi phục sinh đủ trọn vẹn cả 4 lớp bệ móng (F1: 38 cyan, F2: 105 orange, F3: 38 pink, F4: 79 green).
2. **Refactor UI/UX & Chuẩn hóa Kiến trúc MVVM**:
   - Tách biệt $100\%$ ViewModel bằng cách tạo [`AbutmentRebarViewModel.cs`](file:///c:/Users/ADMIN/Downloads/01.%20Cong%20Viec%20DV/Revit%20API/Ph%C3%A1t%20Tri%E1%BB%83n%20REVIT%20API/BIM-DatViet/ViewModels/AbutmentRebarViewModel.cs) và [`RebarLayerItemViewModel.cs`](file:///c:/Users/ADMIN/Downloads/01.%20Cong%20Viec%20DV/Revit%20API/Ph%C3%A1t%20Tri%E1%BB%83n%20REVIT%20API/BIM-DatViet/ViewModels/RebarLayerItemViewModel.cs). Rút gọn Code-behind [`AbutmentRebarView.xaml.cs`](file:///c:/Users/ADMIN/Downloads/01.%20Cong%20Viec%20DV/Revit%20API/Ph%C3%A1t%20Tri%E1%BB%83n%20REVIT%20API/BIM-DatViet/Views/AbutmentRebarView.xaml.cs) từ 864 dòng xuống dưới 50 dòng C# sạch sẽ.
   - Nâng cấp giao diện Quick Setup Panel trực quan: Tự động điều chỉnh Cover Top/Bottom (mm), Spacing, chọn loại thép D12/D16/D20/D25/D32, và danh sách Card F1-F8 có Toggle Switch mượt mà.
3. **Cấu hình Deploy & Target Revit 2025 (.NET 8)**:
   - Đã kiểm nghiệm biên dịch thành công cho **Revit 2025 (`Debug.R25`)** trên .NET 8.0 với `Nice3point.Revit.Sdk` (0 warning, 0 error).
   - Mốc 72/72 là snapshot cũ. Trạng thái hiện tại xem §16: **91/91** unit tests offline.

## 15. Hotfix crash MVVM và quy tắc build/deploy (2026-08-03)

- Không được gọi `NotifyCanExecuteChanged()` từ property getter hoặc từ hàm được command `CanExecute` gọi. `ActionGate` phải là snapshot đã cache; chỉ `UpdateActionGateState()` được phép tính snapshot mới rồi phát thông báo command. Vi phạm quy tắc này tạo vòng lặp `CanExecute -> ActionGate -> NotifyCanExecuteChanged -> CanExecute` và làm Revit stack overflow `0xc00000fd` ngay khi WPF bind cửa sổ.
- Quick Cover phải batch thay đổi các layer và chỉ chạy lại planner một lần; callback từng layer bị chặn trong lúc batch để tránh phân tích lặp và UI giật/đơ.
- UI mố cầu không có checkbox “Kỹ sư xác nhận” hoặc nút Review riêng. Controller tự capture reviewed snapshot ngay trong thao tác Create/Rebuild và kiểm lại trước transaction.
- Không build song song nhiều configuration của cùng project WPF/Revit vì temporary WPF project có thể nhận sai define/API target giữa R25 và R26. Build `Release.R25` và `Release.R26` tuần tự, luôn với `DeployAddin=false` và `LaunchRevit=false`.
- Manifest R25 phải giữ schema tối giản `RevitAddIns -> AddIn`; không thêm node `ManifestSettings`. Sau deploy phải parse XML, xác nhận đúng một `AddIn`, assembly `BIM.DatViet\BIM.DatViet.dll`, rồi khởi động lại Revit để kiểm tra ribbon.
- Snapshot hotfix lúc đó 72/72 test (stale; hiện tại 91/91 — §16). Bằng chứng offline không được dùng thay cho runtime pass trên model mục tiêu.

## 16. Canonical geometry zone và UI capability (cập nhật 2026-08-12)

- **Single source of truth cho Footing zone:** ordered geometry loop của ba nested `PROFILE_TM` station section L/Center/R trong local frame. Zone code vẫn là `GEOMETRY:{Kind}` để giữ metadata compatibility.
- Vai trò L/Center/R suy ra từ station coordinate đã đo, không suy từ type name, mirror hoặc AABB. Solid host chỉ validation/containment; mismatch vượt gate phải chặn Create.
- Analyze có thể gắn `Context` để viewport vẽ host khi status `Ambiguous`; display không mở khóa Create.
- Viewport là passive evidence view: faces là precondition; edges chỉ là decoration. Không có `ZonePicked`, `SetPickTarget`, `PICKED:*` hoặc edge-loop reconstruction.
- UI tự bind canonical Footing sau Analyze và hiển thị ngay proposal F1–F4. Không có bước chọn cạnh/zone trên viewport.
- Cover draft dirty hủy snapshot; `Áp dụng lớp bảo vệ` batch edit rồi planner chạy lại một lần.
- Controller kiểm canonical zone + Host UniqueId + GeometryHash + rule/plan hash trước write. Caller không được bỏ qua.
- Unit test offline hiện tại: **99/99 pass**. Live Create/Rebuild phải smoke riêng trên `SAFE_TEMP_REBAR.rvt`.

## 17. Runtime an toàn và target Revit (2026-08-07)

### Target build/deploy review hiện tại
- Live review workspace: **Revit 2025** (`Release.R25`), DLL:
  `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet\BIM.DatViet.dll`
- Preset live cùng thư mục `Resources\Presets\cau-van-cui-m2.v1.json`.
- Build mặc định: `DeployAddin=false`, `LaunchRevit=false`. Không deploy ngầm từ build.
- **Cấm** script deploy `ForceCloseRevit` khi anh đang mở model gốc. Chỉ deploy khi đã thống nhất process nào được đóng, hoặc khi không còn process cần giữ.

### Process lock (review dual-process)
| PID (ví dụ phiên 2026-08-07) | Document | Rule |
|---|---|---|
| Process anh đang làm mố | `MỐ CẦU VẠN CỦI.rvt` (path gốc dự án) | **CẤM** tắt / save-over / mutate từ agent |
| Process family review | copy `SAFE_MO_CAU_LBH.rfa` trong `tmp/review-workspace/` | Chỉ đọc geometry family |
| Process project review | copy `SAFE_TEMP_REBAR.rvt` trong `tmp/review-workspace/` | Analyze / smoke UI |

Mọi thao tác agent chỉ trên **bản copy** trong `tmp/review-workspace/`. Không mở path gốc `02_Cau_Van_Cui\MỐ CẦU VẠN CỦI.rvt` từ automation.

### Hành vi mở tool và pipeline hiện tại
- Chọn host mố → Analyze tự chạy → extract geometry một lần → resolve measured Footing band → bind canonical zone → lập plan → hiển thị proposal F1–F4.
- Không còn bước chọn cạnh/zone để xem proposal. Create vẫn fail-closed nếu geometry/plan lỗi, zone stale hoặc thiếu `RebarBarType`.
- `requireProfileMassMarkers=false` trên preset pilot: thiếu PROFILE chỉ warning; shared PROFILE nếu đọc được chỉ phục vụ diagnostics.
- Bootstrap/profile resolver lỗi thì command fail-closed và hiển thị `TaskDialog`; không dựng controller giả/default preset để mở UI.
- Viewport được rút về renderer passive, không hard-fail vì không có edge.

### Cleanup kiến trúc đã hoàn thành trong source
1. Xóa fallback PROFILE từ loaded-symbol catalog và reflection geometry không có ownership.
2. `CollectSolids` chỉ còn một nhánh symbol geometry + composed transform; cache invalidate mỗi Analyze.
3. Footing top dùng `AbutmentFootingBandKernel`: chỉ chấp nhận face band đo được, không fallback preset.
4. Zone identity chuyển từ `PROFILE:*` sang `GEOMETRY:Footing`; action-gate và tests dùng canonical geometry evidence.
5. Xóa edge-loop kernel, viewport pick path, FreeForm server no-op và `AbutmentRebarController - Copy.cs`.
6. Viewport tô prism theo `FootingBoundary` thực, overlay proposal F1–F4 và chỉ hỗ trợ orbit/pan/zoom.

### Phạm vi đã chốt và wave tiếp theo
- **Wave hiện tại:** làm chắc F1–F4 Footing, preview/readback/rollback và smoke trên bản copy R25.
- **Chưa claim:** F5–F8, FB4/pile-head cage, hooks/development/continuity, Stem/Backwall/Wingwall và FreeForm.
- Chỉ mở wave capability mới sau khi có detail/evidence, geometry strategy, factory API và readback tương ứng.

### Mindset bắt buộc khi sửa tiếp
1. Engine dùng chung; preset Cầu Vạn Củi chỉ là dữ liệu pilot.
2. Fix nguồn geometry/contract; không thêm fallback giả để làm UI “xanh”.
3. Display được phép khi Ambiguous; Create/Rebuild thì không.
4. Một canonical descriptor cho mỗi zone; `PROFILE_TM` là section/boundary authority, solid là validation authority.
5. Mọi tuyên bố pass phải phân biệt unit test offline, smoke SAFE copy và model gốc của anh.

## 18. Lệnh kiểm tra nhanh

```powershell
# Build R25 không deploy
dotnet build BIM-DatViet.csproj -c Release.R25 --nologo -p:DeployAddin=false -p:LaunchRevit=false

# Unit test offline
dotnet test ..\BIM-DatViet.Tests\BIM-DatViet.Tests.csproj -c Release --nologo
```

Deploy thủ công chỉ khi không đụng process gốc anh; prefer script copy DLL khi Revit review process đã đóng có chủ đích, không `Stop-Process` hàng loạt.



## 19. Hotfix wave F1–F4 (2026-08-10)

### Lỗi đã sửa
1. **`requiredZones` quá rộng chặn Footing-only:** preset đòi Stem/Backwall/Wingwall → classifier `ABUTMENT_ZONE_NOT_UNIQUE` Error → Analyze Ambiguous cả khi F1–F4 đủ geometry. Đã thu hẹp `requiredZones = [Footing]`.
2. **F3/F4 `layerOrder` ngược callout C-C tờ 38:** Top outer phải là F4 (D16 ngang), trong là F3 (D20 dọc). Preset + test contract đã đảo đúng: F4=0, F3=1.
3. **F3/F4 `openingObstaclePolicy=Unresolved`:** host có void/recess làm plan fail. Đã đặt `ApprovedDetail` cho F1–F4 (cùng evidence CVC-P38).
4. **UI lộ zone LOCKED:** `RefreshZoneOptions` chỉ list zone có rule `enabled`; Stem/Wing không còn trong picker wave hiện tại.
5. **Build R25 CS0121 `ToLong` ambiguous:** `ElementIdCompat.ToLong` đổi từ extension sang static method để không đụng Nice3point.Revit.Extensions; callsite đã migrate.
6. **Feedback Create/Rebuild:** `ExecuteWriteCore` cập nhật header/preview theo receipt success/fail.

### Bằng chứng offline
- `dotnet test` Release: **99/99 pass**.
- `dotnet build -c Release.R25 -p:DeployAddin=false -p:LaunchRevit=false`: **0 warning / 0 error**.
- Output: `bin/Release.R25/BIM.DatViet.dll` + preset `ruleVersion 2026-08-10.1`.

### Deploy / smoke
- Process gốc anh (`MỐ CẦU VẠN CỦI.rvt`, Revit 2025) **không** bị agent deploy đè khi đang mở.
- Để nạp bản này: đóng Revit add-in host hoặc copy thủ công DLL+preset vào `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet\`, rồi mở lại Revit và smoke trên `tmp/review-workspace/SAFE_TEMP_REBAR.rvt`.
- Live Create/Rebuild trên SAFE copy **chưa** chạy trong phiên này (MCP bridge timeout; process lock model gốc).

### Vẫn chưa claim
F5–F8, FB4, pile-head cage, hooks/development/continuity, Stem/Backwall/Wingwall, FreeForm, tái sử dụng đa mố ngoài pilot M2.

## 20. Safety gate F1–F4 sau QC độc lập (2026-08-10)

### Source/runtime đã sửa
1. Preset `schemaVersion 2.1`, `ruleVersion 2026-08-10.5`; source evidence có revision, suitability, `authorizedForCreate` và authorization reference.
2. P38 hiện ghi đúng trạng thái `FEASIBILITY_STUDY`, revision `00`, `authorizedForCreate=false`. `requireEvidenceFiles=true`.
3. F1–F4 vẫn giữ mark/đường kính/spacing từ P38 để review, nhưng `Development=Unresolved`, `Continuity=Unresolved`, pile/opening policy `Reject`; thiếu source riêng cho cover 160 và FY400. Preset phải fail-closed, không được claim Create-ready.
4. UI chỉ cho chọn `RebarBarType`; enabled, spacing, cover, layer và clearance là read-only. Controller cũng reject server-side nếu các trường khóa bị sửa.
5. UI tách trạng thái **Thiết kế** và **Runtime**; code evidence/cover/grade/development/continuity được phân loại là lỗi thiết kế.
6. `RebarTypeCatalog` xác minh `StructuralAsset.MinimumYieldStress`; planner/factory chặn type không có vật liệu, không đọc được fy hoặc dưới mức rule.
7. R25/R26 FreeForm dùng một `Line`, `CurveDrivenFreeForm`, `WorkshopInstructions.Straight`; R27 mới dùng overload `BarTerminationsData`.
8. Readback endpoint của thanh thẳng phải khớp reviewed plan trong 2 mm; mismatch là Error và rollback.
9. Receipt JSON phải persist thành công trước `TransactionGroup.Assimilate`; lỗi I/O làm rollback.

### Bằng chứng offline
- `dotnet test` Release: **103/103 pass**.
- `dotnet build` `Release.R25` và `Release.R26`, `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**.

### Blocker để mở gate Create
- Cần phê duyệt kỹ sư cho suitability/revision của P38 dùng automation.
- Cần nguồn/phê duyệt clear cover F1–F4.
- Cần nguồn/phê duyệt mác thép và fy tối thiểu.
- Cần chi tiết development và lap/continuity, gồm chiều dài/vị trí nối và stagger không quá 50%.
- Cần xác nhận cấu tạo cọc–bệ monolithic hoặc chính sách tránh cọc.
- Chưa deploy/smoke vì PID 11156 đang mở model gốc; không được overwrite add-in đang live.

## 22. Safety cutover P0/P1 — trạng thái hiện hành (2026-08-10)

Mục này thay thế các claim runtime/preset tại §7, §19, §20 và §21 nếu có mâu thuẫn.

### Quyết định an toàn

1. Preset pilot dùng `schemaVersion 2.2`, `ruleVersion 2026-08-10.6`; factory ghi receipt với `ToolVersion 1.8.0`.
2. Toàn bộ rule Cầu Vạn Củi, gồm F1–F4, đang `enabled=false`. F1–F4 chỉ giữ bar mark, đường kính và spacing để đối chiếu; geometry template, shape, development, continuity, cover, station datum và xử lý cọc chưa đủ evidence/phê duyệt để Create.
3. Preset không còn rule enabled nên `Validate()` phát `ABUTMENT_ENABLED_RULES_EMPTY`; UI không được mở gate Create/Rebuild.
4. Không còn thuật toán cắt thanh theo disk cọc. `ApprovedDetail` cho pile/opening bị preset validation và planner chặn cho đến khi có solver bảo toàn neo, continuity và thép bù.
5. Đoạn thép bị host clipping làm ngắn hơn `minimumMatPieceLengthMm` là lỗi `ABUTMENT_MAT_PIECE_TOO_SHORT`; planner không được im lặng bỏ đoạn.

### Contract P1 đã có trong engine

- `AbutmentFootingCoverSpec`: cover riêng cho mặt và bốn cạnh của Footing; không dùng một `coverOverrideMm` chung cho mat.
- `AbutmentStationSchedule`: danh sách offset tim thanh tăng nghiêm ngặt từ mép bê tông có projection nhỏ nhất; hỗ trợ layout `StationSchedule` cho Footing.
- `AbutmentSourceEvidence`: revision, suitability, `authorizedForCreate`, authorization reference; rule Drawing chỉ được bật khi source được phép Create.
- Rule enabled phải khai báo provenance cho bar grade/fy, spacing, cover, shape, end geometry, development, continuity và obstacle policy.
- Readback endpoint dùng `topology.revitEndpointShorteningToleranceMm`; pilot đang cấu hình 2 mm.

Các contract trên là capability generic đã compile/test, không phải GeometryPlan Cầu Vạn Củi đã được duyệt.

### Bằng chứng offline của source hiện hành

- `dotnet test` Release: **103/103 pass**, 0 fail.
- `Release.R25`: build thành công, 0 warning, 0 error.
- `Release.R26`: build thành công, 14 cảnh báo `CS0618` từ API Rebar cũ trong adapter, 0 error.
- `Release.R27`: build thành công, 0 warning, 0 error.
- Preset source và bản copy trong output R25/R26/R27 cùng SHA-256 `3e9eb6f3faa48a758c8579db72f838ffd98a6006db871d29ecc6a7e2f9c041e8`.

### Blocker còn lại

- Chưa có approved `CHI TIẾT 1/2`, BBS hoặc GeometryPlan xác định đủ mapping F1–F4, layer, station groups, cover từng mặt/cạnh, bar shape/end treatment, development/lap và pile interaction.
- Chưa lập golden fixture Cầu Vạn Củi vì fixture lúc này sẽ hợp thức hóa giả định thiết kế.
- Chưa deploy/smoke: Revit PID 28668 đang mở model gốc `MỐ CẦU VẠN CỦI.rvt`; MCP chỉ thấy instance PID 2152 ở trạng thái `STALE`. Không reload add-in, không mutate model gốc. Smoke chỉ chạy trên `tmp/review-workspace/SAFE_TEMP_REBAR.rvt` sau khi có runtime bridge sẵn sàng.

## 23. Review thông minh Revit qua BIM765T + add-in (2026-08-11)

### Phân vai bắt buộc

- **BIM765T** là nguồn sự thật cho document/view/rebar/geometry runtime và artifact snapshot Revit. Không dùng source code, shell hoặc viewport WPF để thay cho kết luận trên model.
- **Add-in** chỉ dựng evidence cục bộ từ `AbutmentHostContext` và `AbutmentRebarPlan`: UI đã có selector `X-Ray mố`/`Wireframe` cho passive viewport. Selector chỉ đổi `GeometryModel3D.Material`; không đổi `View.DisplayStyle`, transparency, selection, transaction hoặc bất cứ dữ liệu Revit nào.

### Chuỗi review read-only

1. `revit.inspect_model` → xác lập instance, document, active view, selection và capability.
2. `session.get_task_context`, `document.get_active`, `review.active_view_summary` → khóa document/view context trước khi đọc sâu.
3. Đọc cấu tạo bằng `rebar.audit_host`, `rebar.get_type_readiness`, `rebar.inventory`, `rebar.inspect`, `rebar.inspect_reinforcement`, `rebar.inspect_cover`, `rebar.audit_opening_clearance`; dùng `coordinate.frame_preview` khi cần đối chiếu local frame.
4. `review.capture_snapshot` → artifact review. Snapshot phải ghi document/view, host `UniqueId`, geometry hash, rebar ids/marks, cover/clearance, tool response và giới hạn kiểm tra.
5. Chỉ khi 765T có tool chỉnh display state đã được định vị mới được dùng `preview → policy authorization → execute → verify`; phải làm trên SAFE copy và khôi phục trạng thái view. Không dùng tool mutation không liên quan, như crop/range, để giả lập wireframe/transparency.

### Contract đối chiếu add-in ↔ MCP

Evidence chỉ khớp khi đồng thời khớp: document identity, host `UniqueId`, canonical zone, `GeometryHash`, `PlanHash`/rule version, active-view identity và thời điểm capture. Add-in không gọi MCP trực tiếp hoặc nhận artifact như evidence Create; correlation và quyết định Create vẫn nằm ở controller/snapshot gate.

### Trạng thái runtime phiên này — read-only (2026-08-11)

`revit.list_instances` và `session.get_runtime_health` xác nhận Revit 2025 PID 21816 `READY`, instance `8fb39be4f2ce49f2a965794745377014`, đang mở document gốc `MỐ CẦU VẠN CỦI.rvt` ở `{3D}` (view `3717689`); document đang `IsModified=true`. Chỉ thực hiện read-only, không reload add-in hoặc thay đổi/saved model gốc.

- Host `3770128` `MỐ CẦU M1` là `Generic Models`, `SupportsRebar=true`, `GeometryKind=regular`; trích được 6 solids. Payload `spatial.geometry_extract` trả BBox `[-5.2180, -12.3446, -6.5617]` đến `[60.1900, 73.1828, 26.9248]`; tool không công bố đơn vị nên không dùng các tọa độ này làm kích thước thiết kế.
- `rebar.inventory` và `rebar.audit_host` cùng trả 0 Rebar trên host. Catalog có các `RebarBarType`, nhưng type readiness `Ready=false` vì không có `RebarShape`; đây là blocker runtime độc lập với lock evidence/preset.
- `rebar.inspect_cover` đọc cover chung `DYNAMO = 0.001 m` trên các face đã trả về. Không được dùng giá trị này làm cover thiết kế F1–F4 khi chưa có nguồn/phê duyệt.
- `{3D}` hiện có 19 phần tử hiển thị: 14 `Generic Models`, 2 `Adaptive Points`, 3 datum/internal; 0 selection, 0 warning. `model.snapshot` và `review.active_view_summary` đã có structured readback.
- `review.capture_snapshot` chưa tạo được artifact: input schema không nêu enum `scope`, nhưng các scope phù hợp (`active-view-selection`, `active-view`, `ActiveView`, `view`) đều bị `SNAPSHOT_SCOPE_INVALID`. Đã gửi report tới harness, nhưng không có mã ticket trả về. Không thay bằng export PNG vì đây là FileLifecycle mutation trên model gốc.
- Sau khi anh reload Rebar (2026-08-11), `rebar.audit_host` re-read host `3770128` vẫn trả `RebarShapes=[]`, `Ready=false`, `BlockingReasons=["NO_REBAR_SHAPE"]`; nạp lại chưa đưa được Rebar Shape vào document đang mở. Có 23 `RebarBarType` và 6 Cover Type, nhưng chúng không thay thế Rebar Shape.

## 24. Preflight RebarShape và host — safe fix (2026-08-11)

- Autodesk API xác nhận `Rebar.CreateFromCurves(..., useExistingShapeIfPossible: true, createNewShape: true)` chỉ tạo shape mới khi document đã có tối thiểu một `RebarShape` đủ tham số. `RebarBarType` không thay thế `RebarShape`.
- `AbutmentRebarController.Analyze` chặn fail-closed ngay trong proposal khi host không đạt `RebarHostData.IsValidHost`, hoặc có rule `ShapeDriven` đã bật nhưng catalog `RebarShape` rỗng. `AbutmentRebarFactory.Preflight` kiểm lại catalog trước transaction để chặn thay đổi giữa Analyze và Create.
- `CurveDrivenFreeForm` không bị áp gate `RebarShape`; test model `RequiresExistingRebarShape` khóa đúng điều kiện enabled + ShapeDriven.
- UI ưu tiên nêu lỗi host/RebarShape thay vì hiện “Đề xuất 0 thanh”; lỗi RebarBarType cũng không còn hướng dẫn nhầm rằng RebarShape thay thế type.
- Bằng chứng offline: `dotnet test` Release 109/109 pass; build R25 0 warning/0 error, R26 14 cảnh báo API obsolete/0 error, R27 0 warning/0 error. Không deploy, reload add-in hoặc chạm model gốc.
- Smoke vẫn chỉ được thực hiện khi có bridge Revit hoạt động với `tmp/review-workspace/SAFE_TEMP_REBAR.rvt`; model gốc đang mở và `IsModified=true` tuyệt đối read-only.

## 25. Root cause `Stem`/`Backwall` canonical-zone — preset version drift (2026-08-11)

- Ảnh runtime có `ABUTMENT_ZONE_NOT_UNIQUE` cho `Stem` và `Backwall` không phải lỗi của classifier: preset **live** là schema `2.0`, rule version `2026-08-10.3`, còn source/build là schema `2.2`, rule version `2026-08-10.6`.
- Bằng chứng: SHA-256 DLL live `60360c94…ba515d` khác DLL build R25 `65a2cd22…1a349`; preset live `9e80c959…c681f0` khác source `3e9eb6f3…c041e8`. Preset cũ bật F1–F4 và W1/W2/W3/A1/A2/A3, nên classifier bắt buộc phải tìm Stem/Backwall; geometry runtime chỉ trả Footing và wingwall nên Create bị chặn đúng.
- Không sửa classifier để bỏ qua zone đang được một rule cũ yêu cầu. `AbutmentPresetStore` nay kiểm tra raw payload của file JSON cạnh DLL với embedded preset cùng tên. Lệch một byte sẽ dừng command trước profile selection, nêu rõ cần deploy trọn bộ DLL + Resources; file preset có tên mới và user profiles giữ cơ chế catalog cũ.
- UI/log ghi rõ `profileId`, `schemaVersion`, `ruleVersion`, `ruleHash`, catalog priority/timestamp để ảnh/log lần sau truy được version đang chạy.
- Bằng chứng offline sau sửa: test 110/110 pass; preset source và R25/R26/R27 output cùng SHA-256 `3e9eb6f3…c041e8`; R25 build 0 warning/0 error, R26 14 CS0618/0 error, R27 0 warning/0 error. Không deploy/reload hoặc mutate Revit.

## 26. Deploy R25 và smoke evidence gate (2026-08-11)

- Anh xác nhận duyệt file evidence hiện tại `04a.Theo mo TKCS 1.dwg`, SHA-256 `b85a131f…a8c132`; preset tăng `ruleVersion` lên `2026-08-11.1` và khóa đúng full hash này.
- Test Release 110/110 pass; build `Release.R25` 0 warning/0 error. Package mới có DLL SHA-256 `f37929a2…87ad3a`, preset SHA-256 `6e2d0169…3f9976`; source và output preset trùng hash.
- Vì process model gốc PID `28216` đang `IsModified=true`, không đóng hoặc ghi đè assembly nó đang dùng. Gói mới được deploy side-by-side tại `%AppData%\Autodesk\Revit\Addins\2025\BIM.DatViet.R25.20260811T042926Z`; manifest live trỏ vào thư mục này. Process mới sẽ nạp bản mới; PID `28216` vẫn giữ assembly cũ trong bộ nhớ cho đến khi anh tự lưu/đóng và khởi động lại Revit.
- Smoke trên `tmp/review-workspace/SAFE_TEMP_REBAR.rvt`, PID `45312`, host diagnostic `3770128`: command không còn lỗi `CVC-DWG-CHECK`; dừng đúng tại `ABUTMENT_ENABLED_RULES_EMPTY` vì toàn bộ rule pilot đang khóa chờ phê duyệt thiết kế. Không chạy Create và không mutate model. Hai process SAFE đã đóng; model gốc không bị agent đóng/save.
- Backup manifest trước cutover: `backups/r25-evidence-20260811T042926Z`; backup package live trước rollout đầu: `backups/r25-integrity-20260811T041029Z`.

## 27. Wave F1–F4 đã mở gate và chuẩn bị smoke SAFE (2026-08-11)

### Source, preset và package

- Theo phê duyệt trực tiếp của anh, preset pilot dùng `schemaVersion 2.2`, `ruleVersion 2026-08-11.2`; chỉ `CVC-F1`–`CVC-F4` Footing được bật. F5+ và toàn bộ Stem/Backwall/Wingwall vẫn khóa.
- F1–F4 dùng cấu tạo tờ P38: F1 D20a150 đáy dọc, F2 D32a150 đáy ngang, F3 D20a150 đỉnh dọc, F4 D16a200 đỉnh ngang; outer layer đúng F2/F4 (`layerOrder=0`).
- `AbutmentRebarPlanner` không còn đổi `BarTypeName` của rule clone khi diameter fallback thắng; reviewed rule/hash giữ bất biến, type thực tế nằm trong `PlannedAbutmentRebar`.
- Test `Release.R25`: **110/110 pass**. `dotnet publish` R25/R26/R27 thành công; preset source, output và `publish/Resources` cùng SHA-256 `0e5c9db3a55e68edc9ad8aa936cbffd8cb473aa789a5e8b5579b9b628595966d`.
- Các thư mục nested publish R25/R26 cũ đã được xóa để không còn artifact schema/ruleVersion stale.
- Gói R25 sau QC được deploy side-by-side tại `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet.R25.20260811T065244Z`; DLL SHA-256 `65f8762a685ae919f0075c2ff980a9161ec572a79c5b59d16cfde79463adbf3d`, preset SHA-256 `0e5c9db3…595966d`. Manifest live trỏ vào gói này; process đang chạy chỉ nạp gói mới sau restart.

### Runtime SAFE hiện tại

- Model gốc PID `28216` vẫn mở và không bị agent đóng, save hoặc mutate.
- Bản copy `tmp/review-workspace/SAFE_TEMP_REBAR.rvt`, PID `46144`, đã lưu với host `3770128` đạt `HostSupportsRebar=true`, đủ type D16/D20/D32 và RebarShape `M_00`; `document.get_active` xác nhận `IsModified=false`.
- Anh đang ngồi máy và yêu cầu tự thao tác UI. Smoke đang chờ anh mở đúng cửa sổ `SAFE_TEMP_REBAR.rvt` rồi bấm `DVB_ADDIN → Rải Thép Mố Cầu`; agent không tự click khi anh đang điều khiển máy.
- Sau khi tool mở, phải kiểm tra Analyze/Preview F1–F4, chạy Create trên SAFE, đọc receipt/metadata/centerline/host/type/quantity/layer separation, rồi xác nhận rollback hoặc Rebuild. Chưa được claim live Create pass trước khi hoàn tất chuỗi này.

### QC

- QC độc lập ban đầu phát hiện package R26/R27 và nested publish stale, cùng mutation `BarTypeName` sau khi đóng `RuleHash`.
- Ba finding đã khắc phục; reviewer re-check kết luận **PASS**. Đây là QC source/package, không thay thế smoke Revit đang chờ.

## 28. Sửa encoding và review thực tế F1–F4 sau smoke UI (2026-08-11)

### Kết quả source

- Smoke UI trên SAFE cho thấy 2 blocker dữ liệu: F2 resolve sang type `32M` và F4 dùng `D16`, nhưng cả hai chưa có `Material → StructuralAsset → MinimumYieldStress` để chứng minh CB400-V `fy ≥ 400 MPa`. Gate `REBAR_BAR_TYPE_NOT_FOUND` giữ fail-closed; không thêm fallback giả.
- Đã sửa mojibake tiếng Việt trong `AbutmentRebarPlanner`, `AbutmentRebarController` và `AbutmentRebarViewModel`; grep phạm vi module không còn marker lỗi encoding.
- `ABUTMENT_ITEM_PARAMETER_UNRESOLVED` và `ABUTMENT_PROFILE_MASS_OPTIONAL_MISSING` là Warning đúng: marker thiếu không chặn khi family/type, Rebar host, bê tông, solid, frame, boundary và topology đều hợp lệ.
- `ABUTMENT_PILE_PLACEMENT_MARGIN` trước đây hiện dù F1–F4 dùng `ContinueThroughMonolithicHost`. Preset có computed `[JsonIgnore] UsesPilePlacementMargin`; warning chỉ hiện khi có rule Footing enabled dùng policy `Reject` và margin lớn hơn 0. Property không đi vào serialization hoặc `RuleHash`.
- Preset không có net change: vẫn `ruleVersion 2026-08-11.2`; F1–F4 `clearGapToPreviousMm=0`. Đây là hai phương vuông góc trong cùng mat; `ResolveLayerDepth` với gap 0 vẫn tách tâm bằng tổng bán kính để thanh tiếp xúc nhưng không giao hình học. TCVN 11823-5:2017 §10.3.1.3 về nhiều lớp thanh song song không áp cho cặp F1/F2 hoặc F3/F4.

### Review hồ sơ và tiêu chuẩn

- P38 dự án tiếp tục là evidence Create cho F1–F4. `MACL-iDECO-CD-SB-DR-MO_A1 mẫu tham khảo.pdf` chỉ corroborate cấu tạo mat/bệ cọc và có bar schedule khác; `BV PHAN KE VB.pdf` là kè/tường chắn, không phải evidence mố.
- P39 là Stem/Backwall, không dùng mở wave Footing. F5+ và Stem/Backwall/Wingwall vẫn khóa.
- Full text TCVN 11823-5:2017 đã kiểm qua bản PDF bên thứ ba và đối chiếu record VSQI: §10.3 về cự ly, §11 về neo/nối, §12.3 về cover. P38 không đủ lap length/station; F1–F4 hiện là design-model full run, không được coi là BBS/fabrication detail.
- Structural Asset runtime phải thuộc Material được quản trị/certified là CB400-V; chỉ đặt số `fy=400 MPa` trên material generic không đủ chứng minh chứng chỉ sản phẩm.

### Verification và deploy

- Regression `Release.R25`: **111/111 pass**. Publish R25/R26/R27 thành công; R26 chỉ còn warning CS0618 API cũ đã biết, không có error.
- Preset source, output và publish R25/R26/R27 cùng SHA-256 `0e5c9db3a55e68edc9ad8aa936cbffd8cb473aa789a5e8b5579b9b628595966d`.
- Package R25 mới deploy side-by-side tại `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet.R25.20260811T073739Z`; DLL SHA-256 `6e5ce296f2b0a85cb3607c42257e8495b21d8e476ac9e56c5729c0fd377d32c7`. Process SAFE PID `46144` đang mở tool vẫn giữ DLL cũ; phải đóng/restart SAFE mới nạp package này.
- QC độc lập bản merge kết luận **PASS**, không có finding. Residual blocker duy nhất trước Create là dữ liệu Material/StructuralAsset của `32M` và `D16` trên SAFE, sau đó phải smoke/readback/rollback.

## 29. F1–F4 Create pass trên SAFE bằng centerline FreeForm (2026-08-11)

- Create đầu tiên với `ShapeDriven` tạo đủ 262 phần tử tạm nhưng `ValidateReplacement` phát hiện 16 thanh bị rút ngắn hoặc kéo dài đầu mút so với kế hoạch từ 2,713–45,134 mm (8 shorten, 8 extend; lateral offset đều 0 mm); toàn bộ `TransactionGroup` rollback đúng, không để lại thép dở dang.
- Sai lệch xuất hiện khi Revit khớp thanh thẳng xiên vào RebarShape hiện hữu. Bốn rule F1–F4 đã chuyển sang `CurveDrivenFreeForm`, vẫn là từng thanh `ExplicitStations`, không đổi bar mark, đường kính, spacing, cover, layer order, station hoặc obstacle policy. Preset tăng `ruleVersion` lên `2026-08-11.3`.
- Regression `Release`: **111/111 pass**. Build/publish `Release.R25` thành công. Gói R25 deploy side-by-side tại `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet.R25.20260811T081318Z`; DLL SHA-256 `0eabf534590d06dbe628052994722690522006a06bb64eab240a86dcf6c0a8dd`, preset SHA-256 `87d4e5019b23bfac61248a416ba7595d2389835e0f9d2f826cbcdf7d5ad120cb`.
- Trên `tmp/review-workspace/SAFE_TEMP_REBAR.rvt`, RebarBarType `32M` và `D16` được gán cùng Material đã kiểm chứng của `D20`: `TCVN 1651-2008`, ElementId `89321`, có Structural Asset đạt `fy ≥ 400 MPa`; thay đổi chỉ trên SAFE copy và đã save.
- Smoke cuối trên Revit 2025 PID `37356`, host `3770128`: Analyze mở gate với **262 thanh** (`F1=39`, `F2=105`, `F3=39`, `F4=79`); Create hoàn tất, UI chuyển sang trạng thái “Móng mố đã có Rebar do DVB quản lý” và mở `Tạo lại khu vực`. Theo contract factory, trạng thái này chỉ có sau khi đủ metadata/type/quantity/centerline readback và `TransactionGroup` commit.
- SAFE copy đã save lúc `2026-08-11 15:29:53 +07:00`; ảnh evidence: `tmp/safe-freeform-after-create.png` và `tmp/safe-freeform-model-created.png`.
- Model gốc PID `28216` không bị agent save, đóng hoặc mutate. F5+ và toàn bộ Stem/Backwall/Wingwall vẫn khóa vì chưa đủ path/hook/lap/development evidence; không được claim là đã rải toàn bộ mố.

## 30. Safety cutover P0 toàn bộ Mố Cầu (2026-08-11)

- Thu hồi toàn bộ wave Create cũ. Preset `schemaVersion 2.3`, `ruleVersion 2026-08-11.4`, `ruleHash 0A566A6EE3A44F8236DC1748508C3D9220AC66958EB28A140F9557AECD0D6AF2`; 27/27 rule `enabled=false`.
- P38 source matrix gồm 9 row: F1→A1, F2→A2, F3→A3-T/A3-B, F4→A4, F5→A6, F6→F1, F6A→F2, F7→F3. P39 có 18 row `Proposed` dưới canonical B1…B18. Source/original identity không bị trộn.
- Schema/model/UI/metadata đã thêm source locator và shape signature; action UI ghi rõ `Preset P0 an toàn — chưa cho phép Create`. Validation chặn canonical trùng/sai prefix, thiếu locator, thiếu shape signature, hook/development và nguồn chưa Confirmed.
- Hook adapter được kiểm tra theo contract Revit API: R25/R26 map type/orientation; R27 fail-closed; ShapeDriven truyền explicit `RebarStyle`; FreeForm không nhận hook.
- Regression `Release`: **115/115 pass**. Build `Release.R25`, `Release.R26`, `Release.R27`: 0 warning/0 error.
- Package staging versioned, chưa cài vào Addins: `.deploy-staging/BIM.DatViet.P0.R25.20260811/`. DLL SHA-256 `4734c3fc18a960e006354f7cbb1528d0972942cf08118071d239963dbdf75488`; preset SHA-256 `c75c0416f29a877c77599693fac1dc81e62115f07c707a1b2f8c82baad74910c`; source và package preset trùng hash.
- QC độc lập `QcAbutmentP0`: **PASS**, không có blocker/high/medium; residual đúng chủ đích là R27 chưa map termination, F1/F2 có source mapping Confirmed nhưng vẫn disable/incomplete, và user profile ngoài luồng có thể sửa JSON nhưng runtime vẫn tái validation fail-closed.
- Revit MCP xác nhận process Revit 2025 PID `28656` đang mở model gốc `MỐ CẦU VẠN CỦI.rvt`, `IsModified=false`. Không cài package, reload add-in, save, đóng hoặc mutate process này. Smoke package P0 mới chỉ được chạy trên process/bản copy SAFE sau khi không còn vướng process gốc.

## 31. P0 v8 — source matrix và managed-readback smoke (2026-08-11)

### Source/runtime đã sửa

- `BuildManagedReadbackPreview` đọc Rebar do DVB quản lý theo `AssemblyId` hoặc fallback `HostUniqueId`, resolve được cả key metadata legacy dạng `CVC-F1-CVC-F1`, clone rule trước khi bind type và mở rộng đủ centerline theo `Quantity`.
- Viewport vẫn là evidence renderer passive. Khi hiển thị managed Rebar, X-Ray/Wireframe bỏ surface mesh che khuất nhưng giữ edge/canonical zone; không tạo transaction hoặc sửa model.
- Source matrix P38 hiện khóa đúng identity: F1→A1 và F2→A2 là `Proposed`, station chưa khóa; F3-T là `TopTransverse layerOrder=0`, F3-B là `BottomTransverse layerOrder=1`; F4–F7 ghi `matrix only`. UI dùng nhãn `Canonical … ← Source …`, không trình bày source mark như canonical mark.
- Preset hiện hành: schema `2.3`, rule version `2026-08-11.4`, rule hash `89821469C5231AC86694EA2E0DAA562E683C30881C8EABDA0577B98849BCFB80`; 27/27 rule vẫn `enabled=false`.

### Runtime SAFE và package

- Deploy side-by-side R25 tại `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet.R25.20260811T111718Z`; smoke trên `tmp/review-workspace/SAFE_TEMP_REBAR.rvt`, PID `9532`, host `3770128`.
- UI runtime đọc `Canonical zone: 5`, hiện 9 row source matrix P38, viewport đủ **262 managed Rebar**, đồng thời giữ banner `Preset P0 an toàn — chưa cho phép Create`; Create/Rebuild đều disabled. Log `ABUTMENT_MANAGED_PREVIEW` xác nhận `zone=Footing; bars=262`.
- Model gốc PID `28656` không bị agent đóng, save, reload add-in hoặc mutate.
- Package bàn giao: `.deploy-staging/BIM.DatViet.P0.R25.20260811.8/`; DLL SHA-256 `4eeccf1f7abeea6541889f6e5d513777cc5b0571d4a747d9c7fcb07f2207f6b7`, preset SHA-256 `59e693b4199368a79ed6101e836ec98e1ca75283d7c5fe262799c2ad46a687a0`, manifest SHA-256 `0357d2dc7857dc03816776121a455e69688ecc4a8e10bb695db6fe6c2144eba9`. `cmp` xác nhận DLL/preset trong package trùng source build hiện hành.

### Verification và giới hạn claim

- Unit test Release: **115/115 pass**.
- Build `Release.R25`: 0 warning/0 error; `Release.R26`: 14 warning `CS0618` API Rebar obsolete/0 error; `Release.R27`: 0 warning/0 error.
- Build `Release.R23` hiện không đạt do baseline .NET Framework/API compatibility (`double.IsFinite`, `ArgumentNullException.ThrowIfNull`, `ElementId(long)` và API liên quan); package P0 này chỉ target R25.
- QC độc lập: **Safety PASS**, **traceability mapping PASS** với residual medium, nhưng **visual match PDF chưa đạt**. Viewport hiện là managed-readback, chưa chứng minh pack C-C né cọc 217/170/229 hoặc hình học F4/F5/F6/F6A đúng P38. Không được gọi snapshot này là “thép P0 match PDF”.
- Muốn mở visual pass/Create phải có overlay/section hoặc preview rule-locked đối chiếu P38, khóa station A1/A2 và giải quyết geometry/hook/development/pile interaction. Cho đến lúc đó tiếp tục giữ Create/Rebuild khóa.
- `revit.select_instance` đã thấy PID `9532` `READY`, nhưng `revit.inspect_model` sau đó trả `Transport closed`; lỗi bridge đã báo harness và không được dùng làm evidence runtime.

## 32. Hotfix F3 thiếu thép một đầu do chiếu skew hai lần (2026-08-12)

- Live Revit tạo đủ `F3-T=105` và `F3-B=105`, nhưng lưới ngang chỉ phủ đến khoảng 14.229 mm trên bề rộng chiếu 15.800 mm; đầu xa thiếu khoảng 1.571 mm.
- PROFILE footing là hình bình hành: mép dọc thật 17.433,4 mm và góc 65°, nên `17.433,4 × sin(65°) = 15.800 mm`. Chuỗi F3 `100..15700`, bước 150, đã là offset vuông góc thanh và chừa 100 mm ở mỗi đầu.
- Root cause: hai rule F3 khai báo `referenceAxis=LongitudinalBoundary`, khiến planner nhân `sin(65°)` thêm lần nữa. Đã đổi F3-T/F3-B sang `PerpendicularToBar`; không thay số lượng, spacing, cover hoặc layer order.
- Preset `ruleVersion=2026-08-12.8`, `ruleHash=C1AE64646FED270C59AEB6443E75FF33358FBA8A2662D28A9CACC0D9435B1B47`. Regression Release: **180/180 pass**; build R25: 0 warning/0 error.
- Package R25 deploy side-by-side tại `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet.ProfileFourLayer.R25.20260812.15`; DLL SHA-256 `0A400A7B8EFA8BA66F8E283D3C53271FA676F9DF2D1AC63E971AD657B77E4F65`. Revit PID `44484` vẫn đang nạp `.14`; không tự đóng vì model chưa lưu. Cần restart Revit rồi Rebuild Footing để nghiệm thu live.

## 33. Mốc đo station, cao độ bệ theo PROFILE và khóa mác thép (2026-08-13)

### 33.1 Cạnh gốc đo chuỗi station phải có ngữ nghĩa

- Trước đây `AbutmentRebarPlanner.TryBuildProfileFirstMat` lấy `concreteDatum = polygon.Min(...)` theo dấu
  của `boundary.LongitudinalDirection`, tức theo trục của family instance. Chuỗi F1/F2 không đối xứng gương
  (ba cụm `110..2210`, `2427..3477`, `4040..6290` trên cạnh 6400) nhưng hai đầu mút lại đối xứng, nên đảo
  cạnh gốc vẫn ra đủ 39 chord, `expectedBarCount` vẫn khớp và readback vẫn sạch vì so với chính plan đã lật.
  Không cổng nào bắt được.
- Schema `stationSchedule` có thêm `datumEdge` (`Unresolved` | `ToeSide` | `HeelSide`).
  `AbutmentStationDatumKernel` là kernel thuần: `IsMirrorSymmetric` đo mức lệch khi đọc chuỗi từ cạnh đối
  diện, `TryResolveToeSide` gắn nhãn mũi/gót cho hai cạnh bệ bằng cách so hai vai bệ **đo được** từ
  `FootingPolygonLocal`/`StemPolygonLocal` của các section PROFILE_TM với `toeWidthMm`/`heelWidthMm`.
  Preset chỉ **đặt tên** cho hai vai đã đo; vị trí luôn lấy từ geometry, đúng nguyên tắc PROFILE là authority.
- Tiêu chí gắn nhãn là **khớp thứ tự**: vai rộng hơn nhận khai rộng hơn. Trước 2026-08-15 kernel so tổng
  trị tuyệt đối của hai cách gán; cách đó mù về cấu trúc, không phải vì dung sai. Đặt
  `f(x)=|x−toe|−|x−heel|` thì hiệu hai cách gán bằng `f(vaiMin)−f(vaiMax)`, mà `f` bão hoà ngoài
  `[toe, heel]`, nên khoảng cách giữa hai cách gán **bằng 2 lần phần giao của khoảng đo với khoảng khai**
  và tụt về đúng 0 ngay khi hai khoảng rời nhau. Mố Vạn Củi đo 2100/2300 so với khai 2317/2538 hoà đúng
  455 mm trong khi hai vai chênh 200 mm — gấp 8 lần dung sai. Khớp thứ tự chỉ bí khi không có thứ tự để
  đọc, và cả hai ca đó đã có cổng riêng chặn trước.
- Fail-closed: chuỗi lệch quá `profileToSolidMismatchToleranceMm` mà `datumEdge=Unresolved` →
  `ABUTMENT_STATION_DATUM_EDGE_REQUIRED`. Các mã còn lại: `ABUTMENT_STATION_DATUM_AXIS_UNSUPPORTED`
  (chuỗi không đo theo phương ngang mố), `ABUTMENT_STATION_DATUM_PROFILE_REQUIRED`,
  `ABUTMENT_STATION_DATUM_TOE_HEEL_UNDECLARED`, `ABUTMENT_STATION_DATUM_TOE_HEEL_INDISTINGUISHABLE`,
  `ABUTMENT_STATION_DATUM_EDGE_AMBIGUOUS`, `ABUTMENT_STATION_DATUM_SECTIONS_INCONSISTENT`.
  Chuỗi đối xứng (F3-T/F3-B: `100..15700` bước 150 trên 15800) đi thẳng, không cần khai `datumEdge`.
- **F1/F2 đang bị khóa Create có chủ đích.** P38 ghi ga nguồn nhưng không nói ga 110 đo từ cạnh mũi hay
  cạnh gót, `drawingStationPack`/`sourceLocator.note` cũng không có dấu hiệu nào suy ra được. `datumEdge`
  để `Unresolved`; phải đọc bản vẽ gốc rồi mới điền `ToeSide`/`HeelSide`. Không được đoán.

### 33.2 Cao độ lớp thép bệ lấy theo PROFILE, không lấy theo AABB toàn khối

- `AbutmentKernel.TryResolveFootingBox` có overload nhận `AbutmentVerticalRangeKernel`;
  `AbutmentClassifier` truyền `context.FootingFootprint.Min.Z`/`Max.Z` (đáy PROFILE và `authoritativeTop`).
  Trước đây zone Footing dựng lại từ `overall.MinVertical + FootingDepth`, mà `OverallBox` là hộp bao **mọi**
  solid của host nên mẩu cọc/lớp lót kéo tụt đáy và dịch cả bốn lớp thép cùng một lượng Δ.
- Chỉ zone Footing đổi. Stem/Backwall lấy box từ polygon PROFILE trong `AddProfileCoreZones`,
  Wingwall lấy từ measured surface pair; `TryPartitionCoreZones` không nằm trên đường chạy production.
  Hai overload cũ giữ nguyên hành vi legacy. `ABUTMENT_FOOTING_PROFILE_ELEVATION_INVALID` chặn range suy biến.

### 33.3 Thiếu Structural Asset/fy là Error, không còn Warning

- `AbutmentBarTypeGradeAssessment.MetadataMissing` nay khóa Create ở cả hai chốt:
  planner trả `REBAR_BAR_TYPE_GRADE_METADATA_MISSING` (Error) thay cho Warning cũ, và
  `AbutmentRebarFactory.Preflight` nâng `ABUTMENT_REBAR_GRADE_METADATA_MISSING` lên Error.
  Thông báo nêu rõ RebarBarType nào thiếu Material/Structural Asset, rule nào yêu cầu mác gì và ngưỡng fy.

### Bằng chứng offline

- `dotnet test` Release: **198/198 pass** (baseline trước khi sửa là 180/180).
- Build `Release.R25`: 0 warning/0 error; `Release.R26`: 14 warning `CS0618` API Rebar cũ đã biết/0 error;
  `Release.R27`: 0 warning/0 error.
- Preset lên `ruleVersion 2026-08-13.1`,
  `ruleHash DD72E053DDEEE679AAF9735932975DAFBF48427EC407994C44E3F961F74AA8E0`.
- Chưa deploy, chưa reload add-in, chưa chạm Revit hay model nào.

## 34. Đóng băng bốn lớp thép bệ mố và sửa đổi chỗ toe/heel (2026-08-13)

Mục này là trạng thái preset hiện hành và thay thế mọi claim `ruleVersion`/`ruleHash`/`enabled` mâu
thuẫn ở các mục trước (§7 và §27–§33 đều đã stale ở các con số này). Preset hiện hành: `schemaVersion 2.3`, `ruleVersion 2026-08-13.2`,
`ruleHash 7F06639A8F55622CEB11B744D196E41FC07A607C9E5BE9DF0D7808FA6830F8AA`; **27/27 rule `enabled=false`**.

### 34.1 Bốn rule bệ móng bị đóng băng theo quyết định Owner

Một vòng đối chiếu bản vẽ P38 phát hiện preset sai lệch nhiều điểm, nên Owner khóa toàn bộ bốn lớp
thép bệ móng, không cho Create, tới khi vòng tư vấn kỹ thuật đang chạy song song chốt giá trị đúng.
`disabledReason` của mỗi rule ghi đúng sai lệch của chính nó và điều kiện mở lại:

- **`CVC-F1`** (D20 dọc mặt trên): số thanh không phải 39. Lớp trên bỏ trống hoàn toàn vùng thân mố
  (khoảng trống 1830 mm trên vệt chân thân); mặt bằng D-D đếm 31 thanh, mặt cắt C-C vẽ 33 chấm —
  chưa dứt điểm. Kèm `layerOrder` sai.
- **`CVC-F2`** (D32 dọc mặt dưới): số thanh đúng là 41, preset thiếu hai thanh tại ga 3641 và 3811 vì
  bản vẽ sót nhãn cho một mắt xích 164 mm. Chuỗi ga còn bị lật gương: đang bắt đầu bằng `14@150` tức
  đo từ mép mũi, trong khi D-D/E-E cho thấy phải bắt đầu từ mép gót bằng `15@150`. Chuỗi đúng đo từ
  mép gót: `110 + 15@150 + 229 + 170 + 164 + 7@150 + 217 + 14@150 + 110 = 6400`. `footingCover.faceMm`
  đang 75 trong khi bản vẽ cho 144 mm. Kèm `layerOrder` sai.
- **`CVC-F3-T` / `CVC-F3-B`** (D20 ngang): số thanh 105 và chuỗi ga `100..15700` bước 150 **đã đúng, không
  đổi**. Sai ở `layerOrder` và cover mặt chưa chốt: cover mặt trên khoảng 70 mm, mặt dưới khoảng 124 mm,
  preset đang ghi 75.

`layerOrder` **cố ý chưa sửa** trong đợt này: bản vẽ cho thấy F3 nằm ngoài cùng ở cả hai mặt, F1/F2 nằm
phía trong — ngược hoàn toàn với preset (F1/F2 đang 0, F3 đang 1). Thông tin này chỉ được ghi vào
`disabledReason` để không mất vết; giá trị cuối cùng chốt cùng đợt sửa lớn. `centerlineOffsetsMm`,
`expectedBarCount`, cover, canonical mark và `zoneCode` đều **không** bị sửa trong đợt này.

### 34.2 `toeWidthMm` và `heelWidthMm` bị đổi chỗ — đã sửa

Preset khai `toeWidthMm=2538`, `heelWidthMm=2317`; hai giá trị này **bị đổi chỗ**. Ba nguồn độc lập xác
nhận cạnh 2538 mới là **gót** (phía đường, đất đắp): P35 mặt cắt A-A (tường đỉnh 552 + vai kê gối 993 =
bề dày thân 1545, tường đỉnh về phía 2538, vai kê gối về phía 2317, tường cánh 5000 vươn về phía 2538);
P39 mặt cắt c-c (màn thép chờ phía 2538 cao 6699 mm chạy hết tường đỉnh, phía 2317 chỉ 5419 mm dừng ở
cao độ vai kê); và thép chủ nặng nhất thân mố `W1-D28-150` nằm ở mặt phía 2538, đúng mặt chịu kéo do áp
lực đất. Đã sửa thành `toeWidthMm=2317`, `heelWidthMm=2538`; tổng `2317 + 1545 + 2538 = 6400` vẫn khớp
`footingWidthMm`.

Đây không phải làm đẹp dữ liệu. Consumer duy nhất của hai trường là
`AbutmentRebarPlanner.TryAnchorStationDatum` → `AbutmentStationDatumKernel.TryResolveToeSide`: nó đo hai
vai bệ từ `FootingPolygonLocal`/`StemPolygonLocal` của các section `PROFILE_TM` rồi so với chính hai
trường này để gắn nhãn mũi/gót. Hai trường đổi chỗ làm `toeSide` bị đảo, kéo theo
`datumAtMinimumProjection` đảo, tức lỗi lật gương quay lại nguyên vẹn qua một đường khác. Chênh lệch
221 mm vẫn lớn hơn `ShoulderToleranceMm = 25` nên hai cạnh vẫn phân biệt được sau khi sửa.

Lưu ý: F1/F2 đang có `datumEdge=Unresolved` nên planner hiện fail-closed tại
`ABUTMENT_STATION_DATUM_EDGE_REQUIRED` **trước khi** gọi `TryResolveToeSide`. Vì vậy lần sửa này chưa làm
đổi hành vi runtime hôm nay; nó chặn trước cái bẫy sẽ nổ ngay khi ai đó điền `datumEdge=HeelSide` theo
bản vẽ. Fixture trong `AbutmentStationDatumKernelTests` vẫn dùng số 2538/2317 làm nhãn toe/heel thuần
kernel; test đó không đọc preset nên không bị ảnh hưởng, nhưng đừng đọc nó như khai báo mũi/gót của dự án.

### Bằng chứng offline

- `dotnet test` Release: **198/198 pass**, 0 fail (giữ nguyên baseline 198).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**.
- Preset source và bản copy trong output R25 cùng SHA-256
  `69FA565ED1193C6D520EA4C10BD7ABEC990DB81E11385D3766ED7F8D0ABB9511`.
- `CauVanCuiPreset_DeclaredRuleHashMatchesComputedHash` nay `Validate()` preset **đúng như đóng gói**
  (giữ nguyên `ruleHash` khai báo) và chốt không có `ABUTMENT_RULE_HASH_MISMATCH` lẫn issue Error nào.
  `ABUTMENT_ENABLED_RULES_EMPTY` là Warning đúng chủ đích vì toàn bộ rule đang khóa.
- Repo này **không phải git repository**. Bản sao lưu trước khi sửa:
  `Resources/Presets/cau-van-cui-m2.v1.json.20260813T115700.bak`
  (SHA-256 `C68B22CE43DAC73634191E6476766085002C509D7281B3413B9183B8D782A4DC`).
- Chưa deploy, chưa reload add-in, chưa chạm Revit hay model nào.

## 35. Bộ giải chuỗi ga tổng quát — kernel thuần, chưa nối production (2026-08-13)

Mục này thay thế con số test baseline ở §33/§34 (**198 → 223**). Preset, planner, factory, UI và
extractor **không** bị sửa trong đợt này; §34 vẫn là trạng thái preset hiện hành.

### 35.1 Vì sao cần

Preset pilot liệt kê tường minh 288 con số milimét (F1 39, F2 39, F3-T 105, F3-B 105), trong đó chuỗi
F1/F2 là kết quả nhân tay `sin(65°)` — góc chéo của riêng Cầu Vạn Củi. Bản vẽ gốc chỉ nói ba con số
cho F3 (`100 + 51@150` mỗi nửa) và tám con số nguyên cho F1/F2
(`110, 14@150, 217, 7@150, 170, 229, 15@150, 110`). Mẫu bố trí thép phải mang công thức của bản vẽ,
không mang kích thước của một cây cầu.

### 35.2 Ngôn ngữ mô tả và kernel

`Domain/AbutmentStationLayoutKernel.cs` là kernel thuần (không Revit API, không I/O, chỉ milimét):

- `AbutmentStationLayout` (`:83`) — `Anchor`, `MeasuredAlong`, `AvailableWidthMm`, `Steps`, `Blanks`.
- `AbutmentStationStep` (`:20`) — chỉ dựng được qua `Run` (`:57`), `ContinuedRun` (`:61`),
  `Jump` (`:65`); `Kind` mặc định là `Unresolved` để struct rỗng fail-closed.
- `AbutmentStationBlank` (`:73`) — vùng chừa, **bắt buộc** `Reason` + `AbutmentRuleSourceLocator`.
- `AbutmentStationChain` (`:114`) — offset tăng nghiêm ngặt + `BlankedOffsetsMm` để vết mất thanh
  còn kiểm được.
- `TrySolve` (`:142`) giải theo trục đã khai; `TrySolveNormalToBar` (`:195`) giải rồi chiếu vuông góc
  thanh.

Tái sử dụng, không tạo khái niệm song song: `AbutmentStationReferenceAxis` và
`AbutmentStationDatumEdge` (`Models/AbutmentRebarPresetV1.cs:88,99`),
`AbutmentStationDatumKernel.IsMirrorSymmetric`, `GeometryKernel.ProjectOffsetsNormalToBar` và
`AbutmentRuleSourceLocator`. `Anchor=Unresolved` mang đúng nghĩa §33: chỉ hợp lệ khi chuỗi đối xứng
gương, vì lúc đó hai cạnh cho cùng một bộ thanh.

### 35.3 Ba ca kiểm chứng

`BIM-DatViet.Tests/AbutmentStationLayoutKernelTests.cs`, 25 test:

- **F3 ngang:** `Run(100, 150, 104)` + neo đối xứng tái tạo **đúng tuyệt đối** 105 offset của cả
  `CVC-F3-T` và `CVC-F3-B` đọc trực tiếp từ file preset. Ba con số thay 105 con số.
- **F2 dọc:** tám con số nguyên (`563 = 164 + 170 + 229` theo §34.1) qua kernel rồi chiếu theo hình
  bình hành PROFILE đo được (góc 65° suy ra từ geometry, không gõ `sin(65°)`) tái tạo đúng 39 giá trị
  `centerlineOffsetsMm` của `CVC-F2`, **sai số lớn nhất 0,000000474 mm** — đúng mức làm tròn 6 chữ số
  thập phân của preset. Con số thứ tám (110 mép xa) được tiêu bằng phép kiểm `6400 − 6290 = 110`.
- **F1 dọc có vùng chừa:** cùng một `Steps` (test khẳng định `Assert.Same`) cộng một `blank` cho vệt
  chân thân mố cho 29 thanh so với 39 của lớp dưới, giữ nguyên offset/bước của các thanh còn sống và
  là **tập con** của chuỗi lớp dưới.

**Không** hard-code số thanh lớp trên: vùng chừa trong test là fixture dựng từ `heelWidthMm`,
`footingWidthMm`, `toeWidthMm` và khoảng trống 1830 mm của §34.1; mặt bằng D-D đếm 31, mặt cắt C-C vẽ
33 nên con số thật vẫn mở, test chỉ khóa **tính chất**.

Cảnh báo đọc test: ca F2 ghép chuỗi preset hiện hành với `Anchor=HeelSide` chỉ để thỏa cổng
fail-closed — kernel coi anchor là **nhãn**, không biết cạnh nào là gót. Câu hỏi §34.1 (chuỗi đúng là
41 ga đo từ mép gót) vẫn mở; đừng đọc test này như đã chốt mũi/gót cho F2.

### 35.4 Mã lỗi fail-closed đã thêm

`ABUTMENT_STATION_LAYOUT_UNDECLARED`, `_TOLERANCE_INVALID`, `_AVAILABLE_WIDTH_INVALID`, `_EMPTY`,
`_STEP_UNRESOLVED`, `_RUN_INVALID`, `_JUMP_INVALID`, `_START_REQUIRED`,
`_NOT_STRICTLY_INCREASING`, `_EXCEEDS_AVAILABLE_WIDTH`, `_BLANK_UNJUSTIFIED`,
`_BLANK_RANGE_INVALID`, `_BLANK_REMOVES_EVERY_STATION`. Chuỗi bất đối xứng thiếu anchor dùng lại
`ABUTMENT_STATION_DATUM_EDGE_REQUIRED`; trục đo song song thanh dùng lại
`ABUTMENT_STATION_REFERENCE_INVALID`.

### 35.5 Bằng chứng offline và giới hạn claim

- `dotnet test` Release: **223/223 pass**, 0 fail (baseline trước đó 198/198).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**.
- Kernel **chưa** được `AbutmentRebarPlanner` hay bất kỳ đường chạy production nào gọi; schema preset
  chưa có trường nào cho `Steps`/`Blanks`. Chưa deploy, chưa reload add-in, chưa chạm Revit.
- Giới hạn đã biết: kernel không tự phát hiện khai sai `MeasuredAlong` — đúng cái bẫy chiếu hai lần
  của §32. Chỉ boundary thật của caller phân biệt được, nên bước nối vào planner phải lấy trục đo từ
  PROFILE, không từ dấu trục frame.
- Bước tiếp theo: thêm `stationLayout` vào schema rule như **nguồn** của `centerlineOffsetsMm`, cho
  planner giải qua kernel rồi đối chiếu với chuỗi đang khai (bằng chứng chéo trước khi bỏ chuỗi thô),
  và chỉ sau đó mới chốt số thanh F1 cùng đợt mở băng §34. **Đã làm ở §36.**

## 36. Nối bộ giải chuỗi ga vào preset và planner — di trú có bằng chứng chéo (2026-08-13)

Mục này thay thế baseline test của §35 (**223 → 234**) và các con số `ruleVersion`/`ruleHash` của §34.
Preset hiện hành: `schemaVersion 2.3`, `ruleVersion 2026-08-13.3`,
`ruleHash C0E26B624377835F89F85F82C826465E71889ABFCC9D1455FF4ED58AEBFF14F0`; **27/27 rule vẫn
`enabled=false`**. Đây là bước di trú giữ **cả hai** nguồn để chúng tự soi nhau, không phải bước
thay thế: `centerlineOffsetsMm` vẫn là chuỗi planner dùng để lập kế hoạch.

### 36.1 Schema: `stationSchedule.stationLayout` là trường tuỳ chọn

- `Models/AbutmentRebarPresetV1.cs` thêm `AbutmentStationLayoutSpec` (`anchor`, `measuredAlong`,
  `steps`, `blanks`, `note`), `AbutmentStationStepSpec` và `AbutmentStationStepForm`
  (`Unresolved` | `Run` | `ContinuedRun` | `Jump`; mặc định `Unresolved` để struct rỗng fail-closed),
  `AbutmentStationBlankSpec`. Enum trục đo và cạnh gốc dùng lại `AbutmentStationReferenceAxis` /
  `AbutmentStationDatumEdge`, không tạo khái niệm song song.
- **Không** có trường bề rộng dải bê tông trong JSON. Bề rộng luôn đo từ biên PROFILE rồi quy về
  trục đo bằng chính hệ số chiếu của `GeometryKernel.ProjectOffsetsNormalToBar`, nên không tồn tại
  một hằng số preset nào có thể thắng hình học thật.
- Rule không khai `stationLayout` chạy đúng đường cũ, không đổi một dòng hành vi. Rule enabled ngoài
  lưới thép bệ mà vẫn khai `stationLayout` bị `ABUTMENT_RULE_STATION_LAYOUT_UNSUPPORTED`: chỉ bệ mới
  có hình bình hành PROFILE để đo trục, chỗ khác sẽ mang một mô tả không ai kiểm.

### 36.2 Bốn rule bệ nạp mô tả tái tạo chuỗi ĐANG ĐÓNG BĂNG, không phải chuỗi đúng

| Rule | Mô tả bố trí | Kết quả đối chiếu |
|---|---|---|
| `CVC-F3-T`, `CVC-F3-B` | `anchor Unresolved`, `measuredAlong PerpendicularToBar`, `Run(100, 150, 104)` | khớp 105/105, sai số **0 mm** |
| `CVC-F1`, `CVC-F2` | `anchor ToeSide`, `measuredAlong TransverseBoundary`, `Run(110, 150, 14) + Jump 217 + ContinuedRun(150, 7) + Jump 563 + ContinuedRun(150, 15)` | khớp 39/39, sai số lớn nhất **0,000000473567979 mm** |

- Không rule nào phải bỏ trống: cả bốn đều dựng được `stationLayout` tái tạo đúng chuỗi hiện hành.
- `anchor=ToeSide` của F1/F2 là **nhãn mô tả chuỗi đang đóng băng** theo đúng chẩn đoán §34.1 (chuỗi
  mở đầu bằng `14@150` tức đo từ mép mũi), **không** phải kết luận mũi/gót cho dự án. Chuỗi đúng vẫn
  là 41 ga đo từ mép gót; `note` của từng rule ghi rõ sai lệch của chính nó và dẫn về §34.1.
- `blanks` của F1 cố ý để rỗng: chuỗi 39 ga đang đóng băng không có vùng chừa nào. Vùng chừa vệt chân
  thân mố 1830 mm và số thanh thật (31 hay 33) vẫn mở, chốt cùng đợt mở băng.

### 36.3 Đối chiếu chéo trong planner, trục đo lấy từ PROFILE

- `AbutmentStationLayoutKernel.TryVerifyDeclaredChain` (kernel thuần, có `TryBuildLayout` và
  `TryMeasureAvailableWidth` đi kèm) giải mô tả rồi so từng ga với `centerlineOffsetsMm` đang khai;
  dung sai `DeclaredChainMatchToleranceMm = 1e-3` mm, đủ rộng cho việc preset làm tròn sáu chữ số.
- `AbutmentRebarPlanner.TryCrossCheckStationLayout` gọi kernel ngay sau khi đo `concreteSpanMm` và
  trước `TryAnchorStationDatum`. Trục đo lấy từ `boundary.LongitudinalDirection` /
  `TransverseDirection` của `context.FootingBoundary`, tức polygon PROFILE_TM đã kiểm chứng qua
  `TryResolveProfileFooting` → `GeometryKernel.TryFindPrimaryEdgeDirections`. `context.ProfileFooting`
  rỗng là fail-closed `ABUTMENT_PROFILE_FOOTING_REQUIRED`; **không** lấy tạm dấu trục family instance
  và **không** lấy hằng số góc trong preset — đúng cái bẫy chiếu xiên hai lần của §32.
- Mã lỗi mới: `ABUTMENT_STATION_LAYOUT_CHAIN_MISMATCH` (lệch quá dung sai hoặc lệch số ga; thông báo
  nêu rule, số thứ tự ga, giá trị hai bên và mức lệch mm) và `ABUTMENT_STATION_LAYOUT_ANCHOR_CONFLICT`
  (`stationLayout.anchor` mâu thuẫn `stationSchedule.datumEdge` khi cả hai đã khai — nếu không chặn,
  hai chuỗi lật gương sẽ được so từng phần tử và vẫn qua).
- Bốn rule đang `enabled=false` nên đường planner chưa chạy thật lần nào. Lớp `AbutmentRebarPlanner`
  cũng bị `#if !PROFILE_PLANNER_TESTS` loại khỏi project test vì cần `Document` của Revit, nên cổng
  được kiểm bằng test gọi **đúng hàm kernel mà planner gọi**, với hình bình hành đo được làm fixture.

### Bằng chứng offline

- `dotnet test` Release: **234/234 pass**, 0 fail (baseline trước đó 223/223).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**.
- Preset source và bản copy trong output R25 cùng SHA-256
  `C6866FBD2594786861205ACF73D11534154581A66D9F3617EFC19B7AE8BE7B32`.
- Bản sao lưu trước khi sửa: `Resources/Presets/cau-van-cui-m2.v1.json.20260813T124500.bak`
  (SHA-256 `69FA565ED1193C6D520EA4C10BD7ABEC990DB81E11385D3766ED7F8D0ABB9511`).
- `centerlineOffsetsMm`, `expectedBarCount`, `layerOrder`, cover, canonical mark, `zoneCode`, trạng
  thái `enabled=false` và 23 rule còn lại **không** bị sửa; test
  `AddingTheLayout_ChangedNoFrozenNumberAndNoOtherRule` khóa đúng điều đó.
- Chưa deploy, chưa reload add-in, chưa chạm Revit hay model nào.

### Bước tiếp theo

Đợt mở băng §34 giờ sửa **mô tả** thay vì gõ lại hàng trăm số thực: F2 thêm `Jump 229 / 170 / 164`
đúng vị trí, đổi `anchor` và `datumEdge` sang `HeelSide`, rồi sinh lại `centerlineOffsetsMm` từ chính
bộ giải; F1 chốt vùng chừa vệt chân thân và số thanh; chốt `layerOrder` F3 so với F1/F2 và cover mặt
(F2 144 mm, F3 trên ~70 / dưới ~124). Chỉ sau khi hai nguồn đã đồng thuận qua vài đợt mới bỏ chuỗi
`centerlineOffsetsMm` thô.

## 37. Năm kernel tính theo TCVN 11823-5:2017 — neo, nối, móc, cover, khoảng hở (2026-08-13)

Mục này thay baseline test của §36 (**234 → 543**). Preset, planner, factory, UI, extractor và
schema **không** bị sửa một dòng nào trong đợt này; §34/§36 vẫn là trạng thái preset hiện hành.

### 37.1 Vì sao cần

Preset chỉ khai `development.status = "NoAdditionalLength"` và `requiredLengthMm = 0`, tức phần neo
và nối **bỏ trống hoàn toàn**; cover là con số gõ tay cho một cây cầu. Đây là blocker đã ghi ở §20
và §22 ("cần chi tiết development và lap/continuity", "cần nguồn/phê duyệt clear cover"). Muốn
add-in tự kiểm được cho mố bất kỳ thì các đại lượng này phải tính từ tiêu chuẩn, không tra hồ sơ.

### 37.2 Năm kernel thuần đã thêm

Tất cả trong `Domain/`, không Revit API, không I/O, chỉ milimét và megapascal:

| File | Điều khoản | Hàm công khai |
|---|---|---|
| `AbutmentDevelopmentLengthKernel.cs` | §5.11.2.1, §5.11.2.2, §5.11.2.4, §5.11.5.3.1 | `TryTensionDevelopmentLength` `:288`, `TryCompressionDevelopmentLength` `:313`, `TryHookDevelopmentLength` `:334`, `TryResolveSpliceClass` `:379`, `TryLapSpliceLength` `:446`, `ClassFactor` `:474`, `RoundUpToModule` `:488`, `TryValidateMaterial` `:498` |
| `AbutmentBarBendKernel.cs` | §5.10.2.1, §5.10.2.2, §5.10.2.3 | `TryMinimumBendRadius` `:117`, `TryHookTailExtension` `:187`, `TryHookGeometry` `:230` |
| `AbutmentConcreteCoverKernel.cs` | §5.12.3 | `TryRequiredCover` `:130` |
| `AbutmentBarSpacingKernel.cs` | §5.10.3.1.1, §5.10.3.1.3, §5.10.8 + ràng buộc thi công | `TryMinimumClearSpacingInLayer` `:111`, `TryMinimumClearSpacingBetweenLayers` `:149`, `TryMaximumShrinkageTemperatureSpacing` `:176`, `TryShrinkageTemperatureSteelArea` `:200`, `TryProjectPitchNormalToBar` `:253`/`:288`, `TryClearSpacingFromDimensionedPitch` `:314`, `TryDiagonalMatClearOpening` `:349` |
| `AbutmentLapStaggerKernel.cs` | §5.11.5.3.1 + công nghệ cắt thép | `TryRequiredSpliceCount` `:128`, `TryPlanSingleSplice` `:150`, `TryVerifyDeclaredPieces` `:250`, `RoundToNearestModule` `:286` |

Mặc định **mọi** hệ số điều chỉnh bằng 1,0. Hệ số giảm chỉ được cấp khi người gọi khai kèm hình học
chứng minh: 0,8 đòi `LateralSpacingMm ≥ 150` + `ClearCoverInSpacingDirectionMm ≥ 75` + `d ≤ 36`;
0,75 đòi `SpiralBarDiameterMm ≥ 6` + `SpiralPitchMm ≤ 100`; 0,7 của móc đòi cả hai cover 64/50.
Không có đường nào chỉ cần bật `bool` là được giảm.

### 37.3 Quy tắc làm tròn đã chốt

Bán kính uốn tối thiểu của §5.10.2.3 là **đúng ngưỡng, không dư địa**, nên mọi module trong các
kernel này làm tròn **LÊN**: D28 thép chủ ra 112 mm và phải chi tiết 115 mm, không được xuống 110 mm.
`RoundUpToModule` dùng guard `LengthToleranceMm = 1e-6` mm (một nanomét) chỉ để tiêu nhiễu căn bậc
hai, không chạm dung sai gia công nào. Module: neo/nối 10 mm, móc và bán kính uốn 5 mm, cover 5 mm,
chiều dài cắt 100 mm. Test `BendRadius_IsNeverRoundedBelowTheClauseThreshold` khóa tính chất này cho
cả 11 tổ hợp.

### 37.4 Bộ sinh sơ đồ nối so le

Hai chiều dài cắt, thanh chẵn lắp đảo đầu, nên **không mặt cắt nào** có quá 50% thanh đang nối. Điều
kiện nghiệm thu là của tiêu chuẩn (hai đoạn chênh ít nhất hai lần nối chồng, cả hai trong phôi kho,
tối đa 50%/mặt cắt); còn **chọn điểm chia ở đâu trong dải hợp lệ là chính sách chi tiết**, mang bởi
`SpliceCentreStaggerFraction` mặc định 0,20 (hai tim vùng nối ở 40% và 60% chiều dài thanh), không
phải hằng số ẩn. `ClearGapBetweenSpliceZonesMm` luôn bằng `chiều dài thanh − 2 × đoạn ngắn`.

Ca D32 của kỹ sư ra **đúng tuyệt đối**: thanh 17250, nối 1550 → 7700 và 11100, hở 1850, tỷ lệ 50%.
Ca D20 (nối 630) và D16 (nối 510) lệch 200/300 mm ở đoạn ngắn vì **cặp số kỹ sư đưa không bảo toàn
tổng phôi**: 7000 + 10900 = 17900 ≠ 17880 và 6900 + 10850 = 17750 ≠ 17760. Cặp D16 thậm chí chỉ chồng
500 mm so với 510 mm yêu cầu và bị `TryVerifyDeclaredPieces` bắt đúng — vì vùng nối được dựng từ
`AchievedLapMm` thật, không từ nối chồng yêu cầu. Ghim `ShortPieceOverrideMm` bằng số kỹ sư thì đoạn
dài tái tạo lệch đúng bằng lượng hụt: 20 mm cho D20, 10 mm cho D16, 0 mm cho D32.

### 37.5 Bẫy chiếu xiên, lần thứ ba

`TryClearSpacingFromDimensionedPitch` bắt buộc chiếu bước ghi về phương vuông góc thanh **trước** khi
trừ đường kính, dùng lại `GeometryKernel.ProjectOffsetsNormalToBar` chứ không tạo khái niệm song song.
Bước ghi 150 mm dọc trục cầu với thanh giao 65° chỉ là 136 mm vuông góc; đọc thẳng 150 mm phóng đại
bước 10% và ô thông thuỷ 12%, **về phía không an toàn**. Đây đúng họ lỗi của §32 và §35.5, lần này
chặn bằng API thay vì bằng lời nhắc. Ca kiểm chứng thảm trên: hở trần 116 và 130 giao 65° cho ô thông
thuỷ ~105 mm, đạt so với ngưỡng 75 mm của đầm dùi 50 mm.

### 37.6 Mã lỗi fail-closed đã thêm

`ABUTMENT_REBAR_` `MATERIAL_UNDECLARED` / `BAR_DIAMETER_INVALID` / `CONCRETE_STRENGTH_INVALID` /
`CONCRETE_STRENGTH_OUT_OF_RANGE` / `YIELD_STRENGTH_INVALID` / `YIELD_STRENGTH_OUT_OF_RANGE` /
`BAR_AREA_INVALID`; `ABUTMENT_DEVELOPMENT_` `LIGHTWEIGHT_FACTOR_INVALID` / `WIDE_SPACING_UNPROVEN` /
`SPIRAL_UNPROVEN` / `EXCESS_RATIO_INVALID` / `HOOK_COVER_UNPROVEN`; `ABUTMENT_LAP_`
`SPLICE_REQUEST_UNDECLARED` / `SPLICED_FRACTION_UNRESOLVED` / `AREA_RATIO_EVIDENCE_REQUIRED` /
`CLASS_A_UNPROVEN` / `CLASS_UNDERSTATED`; `ABUTMENT_BEND_` `BAR_DIAMETER_INVALID` /
`ROLE_UNRESOLVED` / `DIAMETER_OUT_OF_TABLE` / `STIRRUP_BAR_TOO_LARGE`;
`ABUTMENT_HOOK_FORM_UNRESOLVED`; `ABUTMENT_HOOK_ROLE_CONFLICT`; `ABUTMENT_COVER_`
`REQUEST_UNDECLARED` / `EXPOSURE_UNRESOLVED` / `BAR_DIAMETER_INVALID` / `AGGREGATE_SIZE_INVALID` /
`WATER_CEMENT_RATIO_REQUIRED` / `WATER_CEMENT_RATIO_OUT_OF_RANGE`; `ABUTMENT_SPACING_`
`BAR_DIAMETER_INVALID` / `AGGREGATE_SIZE_INVALID` / `COMPONENT_THICKNESS_INVALID` / `PITCH_INVALID` /
`ANGLE_INVALID` / `PITCH_BELOW_BAR`; `ABUTMENT_SHRINKAGE_` `SECTION_INVALID` /
`YIELD_STRENGTH_INVALID` / `YIELD_STRENGTH_OUT_OF_RANGE`; `ABUTMENT_MAT_` `CLEAR_SPACING_INVALID` /
`VIBRATOR_DIAMETER_INVALID` / `CROSSING_ANGLE_INVALID`; `ABUTMENT_LAP_STAGGER_`
`REQUEST_UNDECLARED` / `BAR_LENGTH_INVALID` / `LAP_LENGTH_INVALID` / `STOCK_LENGTH_INVALID` /
`CUTTING_MODULE_INVALID` / `FRACTION_INVALID` / `NOT_REQUIRED` / `MULTIPLE_SPLICES_REQUIRED` /
`NO_VALID_SPLIT` / `SHORT_PIECE_INVALID` / `SHORT_PIECE_BELOW_LAP` / `PIECE_EXCEEDS_STOCK` /
`ZONES_TOO_CLOSE` / `TOTAL_BLANK_SHORT`. Trục đo song song thanh dùng lại
`ABUTMENT_STATION_REFERENCE_INVALID`.

### 37.7 Chỗ tiêu chuẩn diễn giải được nhiều cách

1. **Hệ số cấp nối nhân vào ℓ_d đã làm tròn**, không vào giá trị thô. Nhờ vậy nối chồng là bội số
   nguyên của module mà chính thanh đó được neo. Hệ quả: hai hàng bảng vàng thấp hơn 10 mm (D12 ra
   390 thay 400, D32 ra 1540 thay 1550). Nhân vào giá trị thô thì lệch 4 hàng, tới 20 mm.
2. **Đuôi móc 90° lấy 12 d_b cho mọi đường kính** theo §5.10.2.1 (thép chủ). §5.10.2.2 còn cho đuôi
   6 d_b/12 d_b ngắn hơn cho móc đai 90°; đó là cấu tạo khác và kernel **không** trả về.
3. **Dải mác bê tông chặn ở [16; 70] MPa và fy ở [200; 700] MPa.** Tiêu chuẩn không viết thành một
   câu chặn; đây là suy ra từ §5.4.2.1 và họ mác thép thanh.
4. **Tỷ lệ nước–xi măng là bắt buộc**, không mặc định hệ số 1,0, vì tỷ lệ từ 0,50 làm cover tăng 20%
   nên mặc định im lặng là thiếu an toàn.
5. **Sàn 300 mm của nối chồng không bao giờ chi phối** vì ℓ_d đã có sàn 300 mm và hệ số cấp ≥ 1,0.
   Cổng vẫn giữ theo câu chữ điều khoản, và test khóa đúng tính chất này.
6. **Ô thông thuỷ dùng công thức xấp xỉ `min(a, b) × sin θ`** theo đầu bài; đây là ràng buộc thi
   công, không phải điều khoản tiêu chuẩn, và XML doc ghi rõ.

### Bằng chứng offline

- `dotnet test` Release: **543/543 pass**, 0 fail (baseline trước đó 234/234).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**;
  `Release.R26`: 14 warning `CS0618` API Rebar cũ đã biết trong `RebarApiAdapter.cs`/0 error;
  `Release.R27`: **0 warning / 0 error**.
- Bảng vàng `f'c = 30`, `fy = 400`, hệ số 1,0 khớp **đúng tuyệt đối** ở bốn cột neo kéo, neo nén, neo
  móc và bảng uốn/đuôi móc; kernel tính bằng `π d²/4` cho cùng kết quả đã làm tròn như khi dùng diện
  tích danh nghĩa catalogue. Ba sai lệch duy nhất, tất cả đã có test riêng ghi rõ: nối Cấp B D12
  (390 so 400), D32 (1540 so 1550) và đuôi móc đai 135° D14 (84 so 85).
- Kernel **chưa** được `AbutmentRebarPlanner`, factory, UI hay preset gọi. Chưa deploy, chưa reload
  add-in, chưa chạm Revit hay model nào.

### Bước tiếp theo

Đợt mở băng §34 giờ có nguồn tính cho ba blocker của §20/§22: cover F1–F4 tra từ
`AbutmentConcreteCoverKernel` theo cấp phơi nhiễm thật của bệ mố (đúc áp vào đất → 75 mm cơ bản) rồi
đối chiếu với 144 mm của bản vẽ; `development` và `continuity` của bốn rule lấy số từ
`AbutmentDevelopmentLengthKernel` thay cho `NoAdditionalLength`; và thanh dọc 17250 mm của F1/F2 cần
`AbutmentLapStaggerKernel` sinh cặp chiều dài cắt trước khi Create. Trước khi nối vào planner phải
thêm `standard` vào schema rule (mác bê tông, mác thép, cấp phơi nhiễm, tỷ lệ nước–xi măng, cỡ hạt
cốt liệu, chiều dài phôi kho) và giữ **cả hai** nguồn soi nhau như §36 đã làm với chuỗi ga.
**Đã làm một phần ở §38** (số liệu, thứ tự lớp, cover, mở băng F3); phần `standard` vào schema vẫn mở.

## 38. Nạp số liệu chốt bốn lớp thép bệ và mở băng riêng hai lớp F3 (2026-08-13)

Mục này thay mọi con số `ruleVersion`/`ruleHash`/`enabled`/`layerOrder`/cover/`centerlineOffsetsMm`/
`expectedBarCount` của §34 và §36, và thay baseline test của §37 (**543 → 564**). Preset hiện hành:
`schemaVersion 2.3`, `ruleVersion 2026-08-13.4`,
`ruleHash 69CE4A14045AA5D1F8FA32FE71C2EBD569128851E1B0DAE761FCF59477F7ABF8`;
**2/27 rule `enabled=true`** (`CVC-F3-T`, `CVC-F3-B`), 25 rule còn lại vẫn khóa.

### 38.1 Chuỗi ga: F1 và F2 dùng CHUNG một chuỗi 41 ga đo từ mép gót

Chuỗi gốc, đo bằng milimét dọc trục cầu từ **mép gót**, trên bề rộng bệ theo trục đo 6400
(vuông góc thật 5800, góc chéo 65°):
`110 + 15@150` (tới 2360) `+ 229` (2589) `+ 170` (2759) `+ 164` (2923) `+ 7@150` (tới 3973)
`+ 217` (4190) `+ 14@150` (tới 6290) `+ 110 = 6400`.

- **`CVC-F2`** (D32, lớp dưới) dùng nguyên chuỗi: **41 thanh**, không vùng chừa.
- **`CVC-F1`** (D20, lớp trên) dùng **cùng một `steps`**, thêm **một vùng chừa** cho vệt chân thân mố
  nên còn **31 thanh**, và là **tập con** đúng từng chữ số của chuỗi lớp dưới.
- `datumEdge` và `stationLayout.anchor` của cả hai chuyển `Unresolved`/`ToeSide` → **`HeelSide`**;
  câu hỏi mở của §33.1/§34.1 đóng lại tại đây.
- Ba khoảng lệch 229/170/164 thay cho một mắt xích gộp 563 của chuỗi cũ; chính chúng làm xuất hiện
  lại hai thanh mà chuỗi 39 ga đã đánh mất. **Mắt xích 164 mm không có nhãn trên bản vẽ**: nó xác
  định bằng phép đóng chuỗi (`6400 − 6290 = 110`) và đo được 163,9 trên mặt bằng, 163,8 trên mặt cắt.
- `centerlineOffsetsMm` được **sinh lại từ chính `AbutmentStationLayoutKernel`** (giải theo trục đo
  rồi chiếu vuông góc thanh bằng hình bình hành PROFILE đo được), không gõ tay.

**Bẫy đã gặp, phải nhớ:** vùng chừa của F1 khai là **vệt chân thân mố `[2538; 4083]`**
(`heelWidthMm`, `heelWidthMm + stemBaseThicknessMm`), **không** khai dải 2360→4190. Biên vùng chừa
của kernel là **đoạn đóng** (`AbutmentStationLayoutKernel.IsBlanked`), nên khai 2360→4190 sẽ giết
luôn hai thanh nằm đúng trên hai biên và còn **29** thanh thay vì 31 — đúng con số fixture cũ của
§35.3. Dải 1830 mm mà bản vẽ ghi là **hệ quả**: khoảng trống giữa thanh sống ở 2360 và thanh sống ở
4190, lấn ra ngoài vệt chân thân 178 mm phía gót và 107 mm phía mũi (chỗ cho lồng thép chờ thân mố).
Test `TopLongitudinalLayer_SameChainPlusStemBlank_DropsOnlyTheStationsUnderTheStem` khóa cả hai con
số 31 và 29 để lần sau không ai khai lại dải rộng.

`CVC-F3-T`/`CVC-F3-B` giữ **105 thanh**, `Run(100, 150, 104)`, `PerpendicularToBar`,
`anchor Unresolved` — **không đổi một con số nào**; chuỗi này chưa từng bị nghi ngờ qua bất kỳ vòng đo.

### 38.2 Thứ tự lớp đảo lại, và cover định nghĩa theo VỊ TRÍ TIM THANH

Bản vẽ cho F3 là lớp **ngoài cùng** ở cả hai mặt: `CVC-F3-T`/`CVC-F3-B` `layerOrder 1 → 0`,
`CVC-F1`/`CVC-F2` `layerOrder 0 → 1`. Đây là điều §34.1 đã ghi nhận nhưng cố ý chưa sửa.

Bản vẽ ghi **tim thanh**, không ghi cover, nên cover trường chỉ là hệ quả:

| Lớp | Tim thanh | Nguồn | `footingCover.faceMm` |
|---|---|---|---|
| `CVC-F3-T` | 80 mm dưới mặt trên | suy từ tim F1, đo được 76 | 70 (giữ nguyên 70 vì là lớp ngoài) |
| `CVC-F1` | **100 mm dưới mặt trên** | kích thước ghi thẳng P38 | 90 |
| `CVC-F2` | **160 mm trên đáy** | kích thước ghi thẳng P38 | 144 |
| `CVC-F3-B` | 134 mm trên đáy | suy từ tim F2, đo được 149 | 124 |

`Domain/AbutmentMatLayerElevationKernel.cs` là kernel thuần mới giữ đúng phép tính này:
`TryResolveCentroidDepthFromFace` (`:44`) trả `cover + d/2`, và với lớp trong thì lấy `max` với
`tim lớp ngoài + bán kính ngoài + clearGap + bán kính trong`; `FindOverlaps` (`:107`) tìm mọi cặp
thảm **cùng mặt** có tim gần nhau hơn tổng hai bán kính. `AbutmentRebarPlanner.ResolveLayerDepth`
và `ValidateFootingLayerSeparation` nay gọi kernel này, nên bốn vị trí tim **kiểm được offline**;
kernel fail-closed bằng `ABUTMENT_MAT_LAYER_` `COVER_INVALID` / `BAR_DIAMETER_INVALID` /
`CLEAR_GAP_INVALID` / `PREVIOUS_INCOMPLETE` / `PREVIOUS_INVALID`, và planner ghi
`ABUTMENT_MAT_LAYER_DEPTH_UNRESOLVED` (Error) nếu kernel từ chối.

Với bộ số trên, hai cách tính của lớp trong **trùng khít**: F1 ra 100 mm cả từ cover 90 của chính nó
lẫn từ việc chồng lên F3-T; F2 ra 160 mm cả từ cover 144 lẫn từ việc chồng lên F3-B. Đó là bằng
chứng số học rằng bốn cover và thứ tự lớp mới thuộc **một** bộ nhất quán.

Cover cạnh **không đổi**, cả bốn rule vẫn khai 75 mm cho bốn cạnh. Ba trong bốn con số cạnh của bản
vẽ là **hệ quả của chuỗi ga**, không phải giá trị trường: thanh biên F1/F2 có tim cách mép 110 mm
dọc trục cầu (99,7 mm vuông góc) nên đạt cover cạnh 90 mm với F1 và 84 mm với F2; thanh đầu/cuối F3
cách mặt đầu bệ đúng 100 mm vì ga đầu là 100. **Không** được gõ 90/84 vào `transverseStartMm`:
`TryBuildFootingCenterlineBoundary` lùi biên vào `cover + bán kính`, tức 100 mm > 99,693857 mm của ga
đầu, và planner sẽ rơi vào `StationSchedule có tim thanh nằm ngoài boundary sau khi áp dụng cover`.

### 38.3 Ba quyết định Owner đã chốt

1. **Đầu cọc:** bê tông cọc được đập xuống tới xấp xỉ đáy bệ; con số ngàm 600 mm hiểu là **chiều dài
   thép chờ cọc chìa lên bệ** (D32, khoảng 1700 mm, thẳng, không móc), **không** phải chiều cao khối
   bê tông cọc. Nếu hiểu cách khác thì toàn bộ thảm dưới nằm trong bê tông cọc và không thi công
   được. Đây là căn cứ để `pileObstaclePolicy=ContinueThroughMonolithicHost` của F2/F3-B là đúng cấu
   tạo; đã ghi vào `sourceLocator.note` của hai rule đó.
2. **Thép mặt cạnh F4 giữ bước 200 mm** theo bản vẽ; khi kiểm điều khoản thép chống co ngót và nhiệt
   đã dùng cách hiểu **"vùng bề mặt"** (diện tích thép tính cho dải bề mặt, không cho toàn tiết diện
   bệ dày 2000 mm). Rule `CVC-F4` **vẫn tắt** vì thiếu path/cover/station/termination, không phải vì
   spacing; quyết định ghi trong `disabledReason` của chính nó.
3. **Giữ tim F1 ở 100 mm**, không xin điều chỉnh lên 105. Cover mặt trên tới F3-T vì thế là 70 mm,
   **đạt** so với §5.12.3 sau khi áp hệ số tỷ lệ nước–xi măng, **với điều kiện bắt buộc ghi vào chỉ
   dẫn kỹ thuật rằng tỷ lệ nước–xi măng không quá 0,40**. Ràng buộc này nằm trong
   `footingCover.engineerApprovalReference` của F1/F3-T
   (`OWNER-DECISION-2026-08-13-F1-CENTROID-100-WC-RATIO-MAX-040`,
   `…-F3T-COVER-70-REQUIRES-WATER-CEMENT-RATIO-MAX-040`) và trong `sourceLocator.note` của F3-T.
   Số học: cấp ven biển/đúc áp đất cho 75 mm cơ bản, hệ số 0,8 ở tỷ lệ ≤ 0,40 còn **60 mm** ≤ 70 mm;
   ở tỷ lệ 0,50 yêu cầu thành **90 mm** và 70 mm **vi phạm ngay**. Test
   `TopCoverOfSeventyMillimetres_PassesOnlyWithTheDeclaredWaterCementRatio` khóa cả ba nhánh và chạy
   với cỡ hạt 20 và 40 mm để kết luận không phụ thuộc giả thiết cốt liệu.

### 38.4 Mở băng hai lớp F3, giữ băng hai lớp dọc

`CVC-F3-T` và `CVC-F3-B` `enabled=true`, `disabledReason` rỗng. Đủ điều kiện vì: dài khoảng 6250 mm
nên **không cần nối**; chuỗi ga và số thanh chưa từng bị nghi ngờ; và là lớp ngoài cùng nên cao độ
không phụ thuộc hai lớp còn lại.

`CVC-F1` và `CVC-F2` **vẫn `enabled=false`**, nhưng `disabledReason` đã đổi hẳn lý do: hình học của
chúng nay đã đúng, blocker còn lại là **mối nối**. Thanh dọc dài khoảng **17250 mm** trong khi thép
cấp về kho theo cây **11700 mm** nên bắt buộc nối chồng, mà preset chưa khai mối nối nào
(`continuity=Independent`, `stagger=false`) và `development` vẫn `NoAdditionalLength`. Số liệu đã
tính sẵn cho đợt sau: nối chồng **D32 = 1550 mm** cho hai chiều dài cắt **7700 và 11100**;
**D20 = 630 mm**; chiều dài nối chồng làm tròn **LÊN bội số 50 mm** (quyết định đã chốt).
Điều kiện mở lại: khai xong **sơ đồ nối so le** qua `AbutmentLapStaggerKernel`, cập nhật
`continuity` + `development`, rồi bump `ruleVersion`.

### 38.5 Ba việc kiểm tra bắt buộc trước khi kết luận mở băng

1. **Phép kiểm tách lớp `ValidateFootingLayerSeparation`: KHÔNG báo lỗi giả.** Bản cũ dùng
   `FirstOrDefault` cho từng cặp template cố định rồi `continue` khi một bên `null`, nên mỗi mặt chỉ
   còn một lớp thì nó im lặng đúng cách. Vẫn siết lại chứ không nới: phép so giờ nhóm theo **mặt bê
   tông** (`FaceRole`) và so **mọi cặp** trên cùng mặt, nên một thảm thứ ba hoặc hai rule cùng
   template cũng bị bắt; nó vẫn không đòi mặt phải có hai lớp. Một thanh đại diện mỗi rule là **đủ
   chính xác**, không phải lấy mẫu: `LayerDepth` được giải **một lần cho mỗi rule** rồi gán cho mọi
   thanh của rule đó. Test `OneMatPerFace_ReportsNoOverlapWithoutBeingRelaxed` chứng minh sự im lặng
   là do không có cặp, bằng cách thêm một lớp chồng vào đúng hai mặt đó và bắt nó phải báo 2 lỗi.
2. **Cổng mác thép (§33.3) vẫn là lỗi chặn, đúng chủ đích.** `RebarBarType` mà rule resolve được
   nhưng thiếu Material → Structural Asset → `MinimumYieldStress` thì planner trả
   `REBAR_BAR_TYPE_GRADE_METADATA_MISSING` (Error) và `AbutmentRebarFactory.Preflight` trả
   `ABUTMENT_REBAR_GRADE_METADATA_MISSING` (Error). Không nới. Hệ quả cho người dùng nằm ở §38.7.
3. **Cover 70 mm của F3-T: hiện KHÔNG có guard nào chặn**, vì năm kernel §37 vẫn chưa được planner,
   factory, preset hay UI gọi (`grep` chỉ thấy chúng gọi lẫn nhau trong `Domain/`). Đã xử lý bằng
   cách **khai tường minh** tỷ lệ nước–xi măng ≤ 0,40 kèm lý do (§38.3 mục 3) và thêm test chạy
   `AbutmentConcreteCoverKernel` để chốt 70 ≥ 60 khi có tỷ lệ, fail-closed
   `ABUTMENT_COVER_WATER_CEMENT_RATIO_REQUIRED` khi thiếu tỷ lệ, và 70 < 90 khi tỷ lệ 0,50. **Không**
   sửa/nới kernel cover.

### 38.6 Bằng chứng offline

- `dotnet test` Release: **564/564 pass**, 0 fail (baseline trước đó 543/543).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**;
  `Release.R26`: 14 warning `CS0618` API Rebar cũ đã biết trong `RebarApiAdapter.cs` / 0 error;
  `Release.R27`: **0 warning / 0 error**.
- Preset source và bản copy trong output R25 cùng SHA-256
  `57A90DFD06ACBDE39E15736797D235C648414EBE256D0E5BA6096E56FFCA00F6`; `fc /b` không khác byte nào.
- `Validate()` trên preset **đúng như đóng gói** (giữ `ruleHash` khai báo): không `ABUTMENT_RULE_HASH_MISMATCH`,
  không issue Error nào. `ABUTMENT_ENABLED_RULES_EMPTY` **không còn** vì đã có rule enabled.
- Repo này **không phải git repository**. Bản sao lưu trước khi sửa:
  `Resources/Presets/cau-van-cui-m2.v1.json.20260813T135500.bak`
  (SHA-256 `C6866FBD2594786861205ACF73D11534154581A66D9F3617EFC19B7AE8BE7B32`).
- Chưa deploy, chưa reload add-in, chưa chạm Revit hay model nào.

### 38.7 Người dùng cần chuẩn bị gì trong Revit trước khi bấm tạo hai lớp F3

1. Model phải có `RebarBarType` **D20**, và type đó phải có Material gắn **Structural Asset** đọc được
   `fy ≥ 400 MPa` (mác CB400-V). Thiếu Structural Asset là **lỗi chặn**, không phải cảnh báo.
2. Host mố phải đạt `RebarHostData.IsValidHost = true` (family bật `Can Host Rebar`).
3. Không cần `RebarShape`: hai rule F3 dùng `CurveDrivenFreeForm`; gate `REBAR_SHAPE_CATALOG_EMPTY`
   chỉ áp cho rule `ShapeDriven` đang bật.
4. Ba section `PROFILE_TM` L/Center/R phải đọc được, vì cả chuỗi ga và trục đo đều lấy từ chúng.
5. Chỉ dẫn kỹ thuật phải ràng buộc **tỷ lệ nước–xi măng ≤ 0,40** trước khi thi công lớp mặt trên.

### 38.8 Residual đã biết, không được lặng lẽ bỏ

- **Đầu thanh F3 so với mặt xiên:** bản vẽ ghi đầu thanh cách mặt xiên 110 mm dọc trục cầu
  (99,7 mm vuông góc), nhưng engine cắt đầu thanh tại `transverseStartMm + bán kính` = **85 mm**
  vuông góc, tức mỗi đầu dài hơn bản vẽ khoảng 15 mm. Vẫn đạt cover (85 > 75 khai và > 60 yêu cầu),
  nhưng chưa khớp bản vẽ. Chưa sửa trong đợt này vì muốn khớp thì phải khai cover cạnh 89,694 mm —
  một con số không nguồn nào ghi là cover, và `maximumEdgeInset` còn được dùng làm inset khi host
  gồm nhiều component. Cần Owner chốt trước khi đổi.
- Năm kernel §37 vẫn **chưa** nối vào planner/preset; cover, neo và nối vẫn là số khai trong preset
  chứ chưa được tính lại tự động. Đây là việc còn lại của "thêm `standard` vào schema rule".
- `CVC-F1`/`CVC-F2` chưa Create lần nào với chuỗi mới; số 31 và 41 hiện chỉ có bằng chứng offline.

### Bước tiếp theo

1. Deploy R25 side-by-side rồi smoke hai lớp F3 trên bản copy SAFE: Analyze phải ra **210 thanh**
   (105 + 105), Create → readback → receipt, và kiểm mắt thường cao độ 80/134 mm so với hai mặt bệ.
2. Khai sơ đồ nối so le cho F1/F2 bằng `AbutmentLapStaggerKernel` (D32: 7700 + 11100, nối 1550;
   D20: nối 630), cập nhật `continuity`/`development`, rồi mở băng hai lớp dọc.
   **Đã làm phần số liệu ở §39; phần mở băng vẫn CHẶN vì runtime.**
3. Chốt với Owner con số cover cạnh cho đầu thanh F3 (§38.8) trước khi chạm `transverseStartMm`.
   **Owner đã CHẤP NHẬN giữ nguyên và ghi thành tồn đọng — xem §39.6.**

## 39. Khối tham số tiêu chuẩn vào schema và khai mối nối cho hai lớp dọc (2026-08-13)

Mục này thay baseline test của §38 (**564 → 596**) và các con số `ruleVersion`/`ruleHash` của §38.
Preset hiện hành: `schemaVersion 2.3`, `ruleVersion 2026-08-13.5`,
`ruleHash 74F00F97F7A293DFB6CE60721323376EE73D6AC8C2513684F1B7AFE620EE9738`;
**2/27 rule `enabled=true`** (`CVC-F3-T`, `CVC-F3-B`) — **không đổi**, 25 rule còn lại vẫn khóa.
`AbutmentRebarFactory`, `AbutmentRebarPlanner`, UI, extractor và readback **không** bị sửa một dòng nào.

### 39.1 `standard` là khối cấp PRESET, `standardOverride` là ghi đè cấp RULE

Năm kernel §37 cần đầu vào mà preset không có. Khối mới `AbutmentStandardParametersSpec`
(`Models/AbutmentRebarPresetV1.cs:229`) đặt ở **cấp preset** (`:677`) chứ không nhân bản vào 27 rule:
mác bê tông, mác thép, cấp phơi nhiễm, cấp phối và chiều dài phôi kho thuộc **kết cấu và nhà cung
cấp**, không thuộc một bar mark. Rule nào thật sự khác chỉ khai phần khác trong
`standardOverride` (`:630`), mọi trường đều nullable nên ghi đè là **từng trường**, và
`TryResolveStandardParameters` (`:719`) là đường **duy nhất** đọc được giá trị đã hợp nhất.
`AbutmentResolvedStandardParameters` (`:269`) có mọi trường `required` non-nullable, nên một khai báo
thiếu **không thể** bị đọc như một khai báo đủ.

Giá trị đã nạp: `f'c = 30 MPa`, `CB400-V / 400 MPa`, `CoastalOrCastAgainstEarth`,
`waterCementRatio = 0,40`, `maximumAggregateSizeMm = 20`, `stockBarLengthMm = 11700`.
`f'c = 30` **chủ đích** thấp hơn mác C35 trên bản vẽ: hồ sơ mâu thuẫn giữa hai mác nên lấy **biên
dưới**, cho neo và nối **dài hơn**; nâng lên 35 sẽ **rút ngắn** neo/nối nên chỉ được làm sau khi hồ sơ
hết mâu thuẫn.

Fail-closed, mã mới: `ABUTMENT_STANDARD_` `CONCRETE_STRENGTH_REQUIRED` / `BAR_GRADE_REQUIRED` /
`YIELD_STRENGTH_REQUIRED` / `EXPOSURE_REQUIRED` / `WATER_CEMENT_RATIO_REQUIRED` /
`AGGREGATE_SIZE_REQUIRED` / `STOCK_LENGTH_REQUIRED` / `PARAMETERS_INCOMPLETE` (`:949`) /
`SOURCE_REQUIRED` / `SOURCE_INVALID` / `RULE_UNDECLARED`, và
`ABUTMENT_RULE_STANDARD_PARAMETERS_INCOMPLETE` cho rule enabled cần tính neo hoặc nối.
Khai **một nửa** khối cũng là Error: nửa còn lại sẽ được suy từ hư không đúng ngày ai đó bật rule.
**Tỷ lệ nước–xi măng không có mặc định riêng** ở tầng nào: `AbutmentConcreteCoverKernel` đã
fail-closed bằng `ABUTMENT_COVER_WATER_CEMENT_RATIO_REQUIRED` và cổng mới **giữ nguyên** hành vi đó
thay vì để một default cấp preset che đi.

### 39.2 `lapSplice` khai chính sách nối, KHÔNG khai hai chiều dài cắt

`AbutmentLapSpliceSpec` (`:289`) trên rule (`:636`) mang: `requirement`, `splicedFraction`,
`spliceClass`, `stagger`, `lapLengthMm`, `lapDetailingModuleMm`, `cuttingModuleMm`,
`spliceCentreStaggerFraction`. Hai enum mới `AbutmentSpliceRequirement` (`:201`) và
`AbutmentSpliceStagger` (`:213`) đều mặc định `Unresolved` để struct rỗng fail-closed; cột bảng và cấp
nối **dùng lại** `AbutmentSplicedFractionBand` / `AbutmentSpliceClass` của `Domain/`, không tạo khái
niệm song song.

**Hai chiều dài cắt cố ý KHÔNG có trường nào trong JSON.** Chúng suy từ chiều dài thanh, mà chiều dài
thanh đo từ biên PROFILE lúc lập kế hoạch; viết vào preset là đóng băng hình học của một cây cầu vào
mẫu bố trí — đúng cái bẫy §36.1 đã chặn với chuỗi ga. `requirement=NotRequired` tồn tại riêng với
"không khai khối nào": thanh đã **chứng minh** nằm trong phôi kho là một sự thật kiểm được, khối vắng
mặt thì không.

`lapLengthMm` **không phải số gõ tay**: `Validate()` tính lại qua
`AbutmentRebarPresetV1.TryResolveDeclaredLapSplice` (`:815`) →
`AbutmentDevelopmentLengthKernel.TryDetailedLapSpliceLength`
(`Domain/AbutmentDevelopmentLengthKernel.cs:481`, hàm public mới duy nhất của đợt này) và báo
`ABUTMENT_RULE_LAP_LENGTH_MISMATCH` (`:1431`) nếu lệch quá `LengthToleranceMm`. Vòng kiểm này chạy cho
**mọi** rule khai `lapSplice`, **enabled hay không** — rule đang đóng băng đúng là chỗ một số gõ tay
nằm im tới ngày có người mở băng. Mã cùng họ: `ABUTMENT_RULE_LAP_` `REQUIREMENT_UNRESOLVED` /
`STAGGER_UNRESOLVED` / `SPLICED_FRACTION_UNRESOLVED` / `CUTTING_MODULE_INVALID` /
`STAGGER_FRACTION_INVALID` / `SOURCE_MISSING` / `SOURCE_INVALID` / `CONTINUITY_MISMATCH` /
`DEVELOPMENT_MISMATCH` / `LENGTH_UNRESOLVED` / `CLASS_MISMATCH`, cộng
`ABUTMENT_RULE_DEVELOPMENT_LENGTH_MISMATCH` và `ABUTMENT_LAP_DETAILING_MODULE_INVALID`.

### 39.3 Số đã nạp cho F1/F2, và một mâu thuẫn trong bộ số cũ cần Owner chốt

Cấp nối tra bảng §5.11.5.3.1: không quá 50% thanh nối trên một mặt cắt, **không** có hồ sơ tính chứng
minh thép cung cấp trên thép yêu cầu đạt 2 → **Cấp B**, không phải Cấp A.

| Rule | ℓ_d §5.11.2.1 (`development.requiredLengthMm`) | Nối Cấp B ở bước 10 mm | `lapSplice.lapLengthMm` ở bước 50 mm | Hai chiều dài cắt (thanh 17250) | Hở hai vùng nối |
|---|---|---|---|---|---|
| `CVC-F1` D20 | **480** | 630 | **650** | 7200 + 10700 | 2850 |
| `CVC-F2` D32 | **1180** | 1540 | **1550** | **7700 + 11100** | **1850** |

Hàng D32 trùng khít bộ số đã chốt: nối **1550**, cắt **7700 + 11100**, hở **1850**, tỷ lệ nối **0,5**.

**MÂU THUẪN PHẢI GHI RÕ:** §38.4 ghi đồng thời "**D20 = 630 mm**" và "chiều dài nối chồng làm tròn
**LÊN bội số 50 mm**". Hai câu **không thể cùng đúng** vì 630 không phải bội số của 50. Đã nạp
**650 mm** và lý do:

1. Áp bước 50 mm **thống nhất** cho cả hai lớp dọc thì D32 ra **đúng** 1550 mm như bộ số đã chốt.
   Đặt bước 50 cho F2 và bước 10 cho F1 chỉ để khớp cả hai con số là **ép số liệu**, không có căn cứ.
2. 650 > 630 nên lệch về **phía an toàn**; 650 vẫn ≥ yêu cầu điều khoản nên không vi phạm gì.
3. Với thanh 17250 mm, chỉ nối 650 mm mới cho **cả hai** chiều dài cắt nằm trên bước cắt 100 mm
   (7200 + 10700 = 17900 = 17250 + 650); nối 630 mm cho 7200 + **10680**, đoạn dài lệch bước.
   Cùng lý đó với D32: 1550 cho 7700 + 11100, còn 1540 cho 7700 + **11090**.
4. Đọc lại thì "630" là **yêu cầu thô của điều khoản** ở bước 10 mm của chính §5.11.5.3.1, tức giá trị
   **trước** khi áp quy tắc chi tiết 50 mm — không phải giá trị đem chi tiết.

Test `LapOfD20_ExposesTheConflictBetweenTheRecordedNumberAndTheRecordedRounding` **khóa cả hai cách
đọc** để lần sau không ai lặng lẽ chốt một bên. Hai lớp dọc đang đóng băng nên chọn 650 chưa tạo ra
thanh nào; **Owner vẫn cần xác nhận bước làm tròn 50 mm là đúng ý** trước khi mở băng.

### 39.4 CHƯA HIỆN THỰC phần tạo thép — cần Owner quyết, kèm khảo sát

Factory hiện tạo **một** Rebar cho **một** vị trí ga. Mối nối cần **hai** đoạn chồng nhau mỗi ga.

**Phương án A — model hai đoạn chồng nhau.** Đụng vào:

- `Planning/AbutmentRebarPlanner.cs:583,595` — `Key = "{RuleId}-{index:D3}"` và `PlannedQuantity = 1`
  cho từng ga (và bản sao ở `:898,912`, `:1142,1154`). Chưa có khái niệm "đoạn"; muốn có phải thêm
  `pieceIndex` vào `PlannedAbutmentRebar` (`Models/AbutmentModels.cs:308`), rồi giải chiều dài thanh
  **đo được** qua `AbutmentLapStaggerKernel` ngay trong planner.
- `RevitServices/AbutmentRebarFactory.cs:581-591` — **điểm chặn cứng**:
  `group.Count() != ExpectedBarCount` → `ABUTMENT_READBACK_STATION_COUNT`. Nhân đôi số Rebar là
  **hỏng ngay** phép đếm readback; phải đổi ngữ nghĩa sang **số đoạn** = ga × đoạn/ga.
- `Models/AbutmentRebarPresetV1.cs:863-866` (`ABUTMENT_RULE_STATION_COUNT_MISMATCH`) và
  `RebarLayerItemViewModel.cs:61-77` — `expectedBarCount` hiện đồng nghĩa **số ga**, UI cũng hiển thị
  như vậy. Phải tách hai đại lượng chứ không nhân đôi một đại lượng.
- `RevitServices/AbutmentRebarFactory.cs:347-353` (`ABUTMENT_EXISTING_MANAGED_DUPLICATE`, group theo
  `metadata.RuleId` tức `planned.Key`) và `:109` (`plan.Bars.SingleOrDefault(... Key ...)` khi bind
  bar mark). Hai đoạn phải có **Key khác nhau**, nếu không cả hai chỗ này nổ.
- `RevitServices/AbutmentMetadataStore.cs:10-23` — `AbutmentOwnership.RuleId` mang `planned.Key`, nên
  metadata truy nguyên **tự phân biệt được** nếu Key có hậu tố đoạn. Đây là chỗ **rẻ nhất**.

Ba phép kiểm **KHÔNG** báo lỗi giả, đã kiểm chứng chứ không đoán:

- `DetectDuplicates` (`Planning/AbutmentRebarPlanner.cs:1427-1434`) group theo **chuỗi toạ độ đầu
  cuối làm tròn 6 chữ số**, nên hai đoạn khác endpoint không bị coi là trùng dù chồng nhau 1550 mm.
- `ValidateFootingLayerSeparation` (`:1404-1425`) `GroupBy(RuleId).Select(First())` → **một** placement
  cho mỗi rule, và `AbutmentMatLayerElevationKernel.FindOverlaps`
  (`Domain/AbutmentMatLayerElevationKernel.cs:107`) chỉ so **cặp khác rule cùng mặt** theo độ sâu tim.
  Hai đoạn cùng rule cùng cao độ **không** sinh cặp nào.
- `Domain/AbutmentCenterlineReadbackKernel.cs:34` so **từng thanh** planned với actual, không đếm gì.

**Phương án B — một thanh liền, mối nối chỉ ở dữ liệu thống kê.** Không phá giả định nào, nhưng
**vẫn không mở băng được**: bật F1/F2 với `continuity.kind=Lap` đụng
`ABUTMENT_RULE_CONTINUITY_UNSUPPORTED` (`Models/AbutmentRebarPresetV1.cs:1323`) và với
`development.status=RequiredLength` đụng `ABUTMENT_RULE_DEVELOPMENT_UNSUPPORTED` (`:1292`). Hai cổng
đó có sẵn từ trước và **đúng chủ đích**: chúng tồn tại để engine không tạo ra thanh 17,25 m không
thi công được. Phương án B chỉ "gọn" nếu nới hai cổng đó, tức đúng thứ chúng ngăn.

**Đề xuất: Phương án A.** Bảng thống kê là sản phẩm giao cho công trường, và một thanh 17,25 m không
tồn tại; mô hình sai thực tế ở chỗ **người xem tin là đúng** thì tệ hơn mô hình thiếu chi tiết. Khảo
sát cho thấy giá phải trả tập trung vào **một** khái niệm (`pieceIndex`) và **một** phép đếm
(`ABUTMENT_READBACK_STATION_COUNT`), không lan ra trùng lặp / tách lớp / readback centerline.

**Nhưng ĐÃ DỪNG, không tự hiện thực.** Phương án A đổi ngữ nghĩa `expectedBarCount` và cơ chế đếm
readback — hai thứ đang bảo vệ hai lớp F3 **đang chạy thật**. Owner cần quyết ba điều:
`expectedBarCount` giữ nghĩa **số ga** và thêm trường số đoạn, hay đổi thành **số đối tượng**; readback
đếm theo ga hay theo đoạn; và bảng thống kê xuất theo đoạn (F2 thành 82, F1 thành 62) hay vẫn theo
thanh hoàn thiện.

Thêm cổng mới `ABUTMENT_RULE_LAP_SPLICE_UNSUPPORTED` (`Models/AbutmentRebarPresetV1.cs:1374`) để lý do
đóng băng **kiểm được bằng máy**, không chỉ nằm trong `disabledReason`.

### 39.5 Bốn rule bệ: trạng thái cuối

| Rule | `enabled` | Vì sao |
|---|---|---|
| `CVC-F3-T` | **true** | Không đổi một con số nào. Thanh ~6250 mm nằm trong phôi 11700 nên không cần nối; `lapSplice` để **null**. |
| `CVC-F3-B` | **true** | Như trên. |
| `CVC-F1` | **false** | Số liệu ĐÃ ĐỦ (hình học §38 + nối chồng §39). Blocker là **runtime**, không phải dữ liệu. |
| `CVC-F2` | **false** | Như trên. |

Test `LiveTransverseMat_IsUnchangedByThisWave` khóa đúng điều đó cho hai lớp F3: `enabled`,
`disabledReason` rỗng, `layerOrder 0`, cover mặt 70/124, 105 ga, ga đầu 100 và ga cuối 15700,
`lapSplice` null, `standardOverride` null, `development NoAdditionalLength` với 0 mm,
`continuity Independent` với `stagger=false`; và chạy `AbutmentLapStaggerKernel.TryRequiredSpliceCount`
để chứng minh thanh của lớp này **cần 0 mối nối**, chứ không chỉ khai là không cần.

### 39.6 Tồn đọng đầu thanh F3: Owner CHẤP NHẬN, quyết định có chủ đích

Đầu thanh hai lớp F3 dài hơn bản vẽ **khoảng 15 mm mỗi đầu**: bản vẽ ghi đầu thanh cách mặt xiên
110 mm dọc trục cầu (**99,694 mm** vuông góc), engine cắt tại `transverseStartMm + bán kính` = **85 mm**
vuông góc. **Owner đã chấp nhận và quyết định KHÔNG sửa**, ghi thành tồn đọng. Lý do:

1. Muốn khớp bản vẽ thì phải khai `footingCover.transverseStartMm` = **89,694 mm** (99,694 trừ bán
   kính D20). **Không nguồn nào gọi 89,694 mm là cover** — nó là hệ quả của chuỗi ga, đúng họ với ba
   con số cover cạnh mà §38.2 đã từ chối gõ vào preset.
2. Áp **cùng** giá trị đó cho hai lớp dọc sẽ **đẩy thanh biên ra ngoài biên**:
   `TryBuildFootingCenterlineBoundary` lùi biên vào `cover + bán kính`, tức 99,694 mm, trong khi ga đầu
   của F1/F2 đúng bằng **99,693857 mm** — planner rơi vào lỗi tim thanh nằm ngoài boundary sau khi áp
   cover. §38.2 đã ghi đúng cái bẫy này.
3. Hiện trạng **vẫn đạt cover**: 85 mm > 75 mm khai và > 60 mm mà §5.12.3 yêu cầu ở tỷ lệ
   nước–xi măng 0,40. Sai lệch là **khớp bản vẽ**, không phải **an toàn kết cấu**.
4. `maximumEdgeInset` còn được dùng làm inset khi host gồm nhiều component, nên đổi trường này có tác
   dụng phụ ngoài phạm vi hai lớp F3.

Không được lặng lẽ "sửa cho khớp bản vẽ" ở đợt sau: đó là đổi một quyết định Owner đã cân nhắc.

### Bằng chứng offline

- `dotnet test` Release: **596/596 pass**, 0 fail (baseline trước đó 564/564).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**;
  `Release.R26`: 14 warning `CS0618` API Rebar cũ đã biết trong `RebarApiAdapter.cs` / 0 error;
  `Release.R27`: **0 warning / 0 error**.
- Preset source và bản copy trong output R25 cùng SHA-256
  `4E6658D793C42858F9EF438ADBB200BB61743809A3206EE0B5A7F0408034C32A`; `fc /b` không khác byte nào.
  Bản nhúng và bản cạnh DLL cùng sinh từ một `Resources\Presets\*.json` trong `BIM-DatViet.csproj`
  (`Content` + `EmbeddedResource`), nên `AbutmentPresetPackageIntegrity.EnsureMatch` không có gì để bắt.
- `Validate()` trên preset **đúng như đóng gói** (giữ `ruleHash` khai báo): không
  `ABUTMENT_RULE_HASH_MISMATCH`, không issue Error nào.
- Repo này **không phải git repository**. Bản sao lưu trước khi sửa:
  `Resources/Presets/cau-van-cui-m2.v1.json.20260813T145500.bak`
  (SHA-256 `57A90DFD06ACBDE39E15736797D235C648414EBE256D0E5BA6096E56FFCA00F6`).
- Chưa deploy, chưa reload add-in, chưa chạm Revit hay model nào.

### Bước tiếp theo

1. **Owner quyết ba câu hỏi ngữ nghĩa của §39.4** (`expectedBarCount`, phép đếm readback, bảng thống
   kê theo đoạn hay theo thanh) trước khi ai đó sửa factory. **Owner đã quyết — xem §40.**
2. **Owner xác nhận bước làm tròn nối chồng 50 mm** (§39.3), tức D20 là 650 mm chứ không phải 630 mm.
   **Owner đã xác nhận — xem §40.**
3. Deploy R25 side-by-side rồi smoke hai lớp F3 trên bản copy SAFE — việc còn nợ từ §38, đợt này
   **không** làm thay đổi gì cho hai lớp đó.
4. Nối `AbutmentConcreteCoverKernel` và `AbutmentBarSpacingKernel` vào planner để cover và khoảng hở
   được **tính lại** thay vì chỉ khai; khối `standard` giờ đã có đủ đầu vào cho việc đó.

## 40. Dựng hai đoạn thép cho mỗi vị trí thanh và mở băng hai lớp dọc (2026-08-13)

Mục này thay baseline test của §39 (**596 → 618**), các con số `ruleVersion`/`ruleHash`/`enabled`
của §39, và **huỷ** kết luận "chưa hiện thực phần tạo thép" của §39.4. Preset hiện hành:
`schemaVersion 2.3`, `ruleVersion 2026-08-13.6`,
`ruleHash 8B086D3BE531914DCA6047172CB906E2ADF7AA3BD376B8A6599C148B48648FDE`;
**4/27 rule `enabled=true`** (`CVC-F1`, `CVC-F2`, `CVC-F3-T`, `CVC-F3-B`), 23 rule còn lại vẫn khóa.
Đây là **Phương án A** của §39.4, hiện thực đúng theo bốn quyết định Owner.

### 40.1 Bốn quyết định Owner và cách hiện thực

1. **`expectedBarCount` giữ nghĩa SỐ VỊ TRÍ THANH** — 41 cho F2, 31 cho F1 — vì đó là con số bản vẽ
   ghi và là thứ truy nguyên được về hồ sơ. **Không** đổi ngữ nghĩa trường cũ, **không** đụng UI
   `RebarLayerItemViewModel.DistributionText`. Số đối tượng Rebar thật nằm ở trường **suy ra**
   `AbutmentZoneRule.ExpectedRebarObjectCount` (`Models/AbutmentRebarPresetV1.cs:670`) =
   `expectedBarCount × PiecesPerBarPosition` (`:661`). Cả hai `[JsonIgnore]` nên **không** vào
   `ruleHash`. Suy ra thay vì lưu: hai chiều dài cắt đến từ chiều dài thanh đo lúc lập kế hoạch, nên
   một con số lưu sẵn chỉ là chỗ thứ hai cho cùng một sự thật sai.
2. **Đọc lại kiểm HAI TẦNG.** Xem §40.3.
3. **Dữ liệu đủ dựng bảng thống kê** ghi lên từng đoạn. Xem §40.4.
4. **Bước làm tròn nối chồng 50 mm** — giữ nguyên, không đụng. D20 = 650 mm, D32 = 1550 mm.

### 40.2 Khái niệm "đoạn": kernel thuần `AbutmentBarPieceKernel`

`Domain/AbutmentBarPieceKernel.cs` là kernel thuần mới (không Revit API, không I/O, chỉ milimét).
Nó **dùng lại** `AbutmentLapStaggerKernel.TryPlanSingleSplice` để giải cặp chiều dài cắt và chỉ thêm
phần đặt cặp đó lên từng vị trí thanh:

- `AbutmentBarPiece` (`:9`) — `PositionIndex`, `PieceIndex`, `PieceCount`, `StartOffsetMm`,
  `EndOffsetMm`, `LapLengthMm`, `ReversedLayout`.
- `SinglePiecePlan` (`:114`) — hình dạng của **mọi** rule trước đợt này: một đoạn mỗi vị trí.
- `TryPlanSplicedPositions` (`:142`) — giải **một** cặp cắt cho cả lớp, từ vị trí **dài nhất** đo
  được, nên mọi vị trí đều với tới đầu kia và vị trí ngắn hơn chỉ chồng **nhiều hơn** nối yêu cầu.
- `TryLayOutPosition` (`:238`) — vị trí **lẻ** lắp đoạn ngắn trước, vị trí **chẵn** đảo đầu; đúng
  một phép luân phiên đó thoả cả hai ghi chú bản vẽ.
- `TryVerifyMeasuredPositions` (`:307`) — phép đọc lại hai tầng.
- `FormatPieceKey` (`:381`) — `{RuleId}-{vị trí:D3}` khi `pieceCount ≤ 1`, `…-P{đoạn:D1}` khi nhiều
  hơn. Rule không nối giữ **nguyên chuỗi khoá cũ từng ký tự**.

`Models/AbutmentModels.cs:334-355` thêm bảy trường vào `PlannedAbutmentRebar`. Không trường nào nằm
trong `ComputePlanHash`, nên plan hash của hai lớp F3 **không đổi**.

`Planning/AbutmentRebarPlanner.cs:659` `TryResolveBarPiecePlan` là chỗ duy nhất quyết định số đoạn.
Chiều dài thanh **đo từ hình học thật** (`:687-690`: chuẩn của từng đường tim đã clip), **không**
lấy hằng 17250 từ preset — đúng nguyên tắc §36.1/§39.2. Ba chỗ sinh khoá của planner đều đã sửa:
`:621` (bệ, có đoạn), và hai chỗ mặt (`ProfileClippedSurface`, `SolidClippedSurface`) chỉ thêm
`PositionIndex = index`, giữ nguyên khoá.

**Chi tiết giữ hành vi:** hai đầu ngoài của một vị trí dùng **chính** hai điểm đo được
(`Planning/AbutmentRebarPlanner.cs:613-618`), không dựng lại qua vòng đổi đơn vị milimét. Dựng lại
sẽ dịch thanh một đoạn epsilon và làm đổi `planHash` của hai lớp F3.

**Fail-closed đã thêm:** `ABUTMENT_MAT_STATION_FRAGMENTED` (`:582`, station bị host cắt rời thì
không giải nối), `ABUTMENT_BAR_PIECE_` `BAR_LENGTH_NOT_UNIFORM` (lớp không phải một cặp cắt duy
nhất, dung sai `BarLengthUniformityToleranceMm = 1,0` mm) / `SPLICED_FRACTION_EXCEEDED` /
`SPLICE_ZONES_OVERLAP` / `SPLICED_FRACTION_UNRESOLVED` / `PIECE_EXCEEDS_POSITION` / `LAP_SHORT` /
`POSITION_COUNT` / `COUNT` / `POSITION_INDEX_INVALID` / `BAR_LENGTH_INVALID` /
`POSITION_COUNT_INVALID` / `REQUEST_UNDECLARED` / `PLAN_UNDECLARED` / `READBACK_UNDECLARED` /
`EXPECTATION_INVALID` / `TOLERANCE_INVALID` / `LAP_LENGTH_INVALID` /
`STANDARD_PARAMETERS_INCOMPLETE` / `LAP_LENGTH_UNRESOLVED`. Đoạn vượt phôi kho vẫn do
`ABUTMENT_LAP_STAGGER_PIECE_EXCEEDS_STOCK` của kernel cũ bắt.

### 40.3 Phép đọc lại: hai tầng, chặt hơn chứ không lỏng hơn

`RevitServices/AbutmentRebarFactory.cs:600` `ValidateStationPositionsAndPieces` thay chỗ chặn cứng cũ
(`group.Count() != ExpectedBarCount`). Nay kiểm:

1. **Số vị trí thanh** = `expectedBarCount` (đếm `PositionIndex` phân biệt).
2. **Mỗi vị trí đúng số đoạn** đã lập kế hoạch.
3. **Hai đoạn chồng nhau đủ nối chồng**: `TryMeasurePieceAlongPosition` (`:666`) chiếu đường tim
   **thật** của Rebar lên trục của chính vị trí đó, nên hai đoạn thành hai khoảng trên một trục số và
   độ chồng là một phép trừ. Dung sai `PieceOverlapToleranceMm = 2` mm (`:659`), cùng bậc với dung
   sai readback đường tim.

Mã mới: `ABUTMENT_READBACK_BAR_PIECE` (`:648`), `ABUTMENT_READBACK_PIECE_UNMEASURABLE` (`:634`).
`ABUTMENT_READBACK_STATION_COUNT` giữ nguyên tên.

**Bằng chứng rule không có mối nối giữ nguyên hành vi từng dòng:**

- `PiecesPerBarPosition == 1` → `PositionIndex` = đúng biến đếm `index` cũ, nên "số vị trí phân biệt"
  = `group.Count()` cũ; **thông điệp lỗi giữ nguyên từng ký tự** (`:614`, nhánh `piecesPerPosition == 1`).
- Tầng 2 với một đoạn mỗi vị trí **không thể** thất bại thêm: mỗi bar có `PositionIndex` riêng.
- Tầng 3 **không chạy**: đường một đoạn không đo hình học gì cả (`:625-630`), nên polyline nhiều
  điểm của hai strategy mặt không sinh lỗi mới.
- Khoá bằng test: `LiveTransverseMat_IsUnchangedByThisWave` (105 vị trí, 105 đoạn, khoá
  `CVC-F3-T-001`…`-105`, tim 80/134 mm, `TryRequiredSpliceCount = 0`) và
  `UnsplicedLayer_KeepsOnePiecePerPositionAndTheKeyItAlwaysHad`.

### 40.4 Dữ liệu ghi lên mỗi đoạn thép

`RevitServices/AbutmentMetadataStore.cs:111` `EvidenceJson` nay nhận `PlannedAbutmentRebar` thay vì
`AbutmentZoneRule` và thêm khối `Piece` cùng cả khối `lapSplice` của rule. **Không** đổi schema
ExtensibleStorage (GUID `213A8D44-…` giữ nguyên 13 trường) — thêm field vào schema đã tồn tại trong
model cũ là lỗi runtime; khối mới nằm gọn trong `SourceEvidenceJson`.

Khối `Piece`: `PositionIndex`, `PieceIndex`, `PieceCount`, `PieceLengthMm`, `PositionLengthMm`,
`LapLengthMm`, `ReversedLayout`, `SpliceCountPerPosition`, `BarPositionCount`, `RebarObjectCount`,
`CutPairShortMm`, `CutPairLongMm`, `DiameterMm`, `BarTypeName`, `BarTypeInModel`.

Đối chiếu với cột bảng khối lượng cốt thép mố của hồ sơ tham khảo:

| Cột | Trạng thái | Nguồn |
|---|---|---|
| Số hiệu | **đủ** | `SourceDrawingMark` + `CanonicalMark` (đã có từ trước) |
| Đường kính | **đủ** | `Piece.DiameterMm`, `Piece.BarTypeInModel` |
| Bán kính uốn | **đủ cho thanh thẳng** | `StartEndGeometry`/`EndEndGeometry.hookSpec.bendRadiusMm`; bốn lớp bệ đều `kind=None` nên bằng 0. Thanh có móc sau này cần `AbutmentBarBendKernel`, **chưa nối** |
| Số lượng | **đủ** | `BarPositionCount` (bản vẽ) và `RebarObjectCount` (model) |
| Chiều dài một thanh | **đủ** | `PositionLengthMm` (thanh hoàn thiện), `PieceLengthMm` (đoạn cắt) |
| Số mối nối | **đủ** | `SpliceCountPerPosition` |
| Chiều dài mối nối | **đủ** | `LapLengthMm` |
| Tổng chiều dài | **đủ** | tổng `PieceLengthMm` của mọi đoạn |
| Khối lượng | **THIẾU** | cần khối lượng đơn vị kg/m hoặc khối lượng riêng thép; preset chưa khai trường nào |

Bảng cắt phôi cho xưởng: `CutPairShortMm`/`CutPairLongMm` ghi thẳng trên **mỗi** đoạn, nên hai chiều
dài cắt truy được kể cả khi chỉ đọc một đối tượng.

### 40.5 Ba cổng `_UNSUPPORTED` — gỡ một, siết hai

- `ABUTMENT_RULE_LAP_SPLICE_UNSUPPORTED` (§39.4) **đã gỡ hẳn**: cổng tạm, lý do đã hết.
- `ABUTMENT_RULE_CONTINUITY_UNSUPPORTED` (`Models/AbutmentRebarPresetV1.cs:1356`) **thu hẹp**: chỉ
  im lặng khi `Kind=Lap` **và** `TargetZone == rule.Zone` **và** `lapSplice.Requirement=Required` —
  đúng thứ planner dựng được. Continuity liên vùng và coupler **vẫn chặn**.
- `ABUTMENT_RULE_DEVELOPMENT_UNSUPPORTED` (`:1316`) **thu hẹp**: chỉ im lặng khi chiều dài neo là
  **nguồn của một `lapSplice` Required**. Runtime vẫn **không** kéo dài đầu thanh theo development
  length khai riêng, nên mọi khai báo khác vẫn chặn.
- Cổng **mới** `ABUTMENT_RULE_LAP_STAGGER_UNSUPPORTED` (`:1411`): rule nối phải khai
  `stagger=ReversedEndForEnd`; `None` sẽ dồn mọi mối nối lên một mặt cắt.

Test `LappedMatIsBuildable_WhileEveryOtherContinuityAndDevelopmentStaysBlocked` bật từng lớp rồi đưa
lần lượt bốn cấu hình lân cận về đúng cổng của nó, nên việc thu hẹp **kiểm được bằng máy**.

`Development.RequiredLengthMm` và `Continuity` vẫn **chỉ** được `Validate()` đọc; planner và factory
không tiêu thụ hai trường này (đã `grep` xác nhận). Chúng là khai báo, không phải chỉ thị hình học.

### 40.6 Hệ quả đã lường của việc bật F1

`CVC-F1` là lớp trên, `pileObstaclePolicy=Reject`, nên bật nó **kích hoạt**
`preset.UsesPilePlacementMargin`: planner cộng thêm `PilePlacementMarginMm` vào clear cọc và ghi
issue Info `ABUTMENT_PILE_PLACEMENT_MARGIN`; `AbutmentGeometryExtractor` cũng nới bán kính tìm cọc vì
đường kính lớn nhất trong các rule đang bật lên D32. Đây là hành vi **đúng cấu tạo**, không phải
regression; test `PilePlacementMargin_IsUsedOnlyByEnabledFootingRejectRules` khoá đúng F1 là rule duy
nhất đang bật yêu cầu margin.

Mỗi mặt bê tông nay có **hai** thảm thật, nên `ValidateFootingLayerSeparation` chạy lần đầu trên
cặp thật thay vì trên tập rỗng. Cả hai cặp **đạt đúng bằng** tổng hai bán kính (trên 100−80 = 20 =
(20+20)/2; dưới 160−134 = 26 = (20+32)/2), tức hai thảm **tiếp xúc, không cắt nhau** — đúng bộ số
§38.2 và **không còn dư địa nào**. Test `BothMatsOfEachFace_ArePairedAndTheOverlapCheckStillFires`
khoá cả đẳng thức lẫn việc cổng vẫn nổ khi dịch lớp trong 1 mm.

### Bằng chứng offline

- `dotnet test` Release: **618/618 pass**, 0 fail (baseline trước đó 596/596).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**;
  `Release.R26`: 14 warning `CS0618` API Rebar cũ đã biết trong `RebarApiAdapter.cs` / 0 error;
  `Release.R27`: **0 warning / 0 error**.
- Preset source, bản copy trong output R25 và **bản nhúng trong DLL** cùng SHA-256
  `6EBD09430FB6498DE588EFC5779E71026F63F3830F0291AC24C49180C6D2949B`; `fc /b` không khác byte nào.
- `Validate()` trên preset **đúng như đóng gói** (giữ `ruleHash` khai báo): không
  `ABUTMENT_RULE_HASH_MISMATCH`, không issue Error nào.
- Repo này **không phải git repository**. Bản sao lưu trước khi sửa:
  `Resources/Presets/cau-van-cui-m2.v1.json.20260813T152644.bak`
  (SHA-256 `4E6658D793C42858F9EF438ADBB200BB61743809A3206EE0B5A7F0408034C32A`).
- Chưa deploy, chưa reload add-in, chưa chạm Revit hay model nào.

### 40.7 Người dùng cần chuẩn bị gì trong Revit để tạo hai lớp dọc

Ngoài năm việc của §38.7 (áp cho cả bốn lớp), thêm:

1. Model phải có **cả** `RebarBarType` **D20** và **D32**, và **cả hai** phải có Material gắn
   Structural Asset đọc được `fy ≥ 400 MPa`. Thiếu là **lỗi chặn**, không phải cảnh báo.
2. Số đối tượng Rebar sẽ **gấp đôi** số thanh bản vẽ ở hai lớp dọc: F2 ra **82** đối tượng cho **41**
   thanh, F1 ra **62** đối tượng cho **31** thanh. Cột số lượng trên bảng lớp thép hiện ghi rõ
   "41 thanh, 82 đoạn". Đây là **đúng**: một thanh dài 17,25 m không tồn tại ngoài công trường.
3. Tổng cả bốn lớp bệ: **354** đối tượng Rebar (62 + 82 + 105 + 105) cho **282** thanh bản vẽ.
4. Bệ mố phải là **một khối liền** trong vùng thảm: nếu host cắt một ga thành hai mẩu rời, engine
   khóa bằng `ABUTMENT_MAT_STATION_FRAGMENTED` chứ không tự nối bừa.

### 40.8 Residual đã biết, không được lặng lẽ bỏ

- **Chưa Create lần nào trên Revit thật.** Cả bốn lớp bệ mới chỉ có bằng chứng offline; §38.8 và §39
  còn nợ smoke F3 trên bản copy SAFE. Đây là giới hạn **giống hệt** lần mở băng F3 ở §38, không phải
  giới hạn mới của đợt này.
- **Cặp cắt 7700/11100 gắn với chiều dài thanh 17250 mm.** Planner đo chiều dài thật lúc lập kế
  hoạch; nếu hình bình hành PROFILE cho chiều dài khác thì cặp cắt sẽ khác, và đoạn dài chỉ nằm trên
  bước cắt 100 mm khi `chiều dài thanh + nối chồng` là bội số của 100. Kernel **không** ép; nó chỉ
  bảo đảm đoạn ngắn trên module và cả hai trong phôi kho. Số thật phải đọc từ receipt sau Create
  đầu tiên.
- **Cột khối lượng của bảng thống kê còn thiếu dữ liệu** (§40.4).
- Năm kernel §37 vẫn **chưa** nối vào planner cho cover và khoảng hở (§39 bước 4 còn mở).
- Tồn đọng đầu thanh F3 của §39.6 **giữ nguyên**, Owner đã chấp nhận.

### Bước tiếp theo

1. Deploy R25 side-by-side rồi smoke **bốn** lớp bệ trên bản copy SAFE: Analyze phải ra **354** đối
   tượng, Create → readback → receipt, rồi đọc receipt lấy chiều dài thanh dọc **đo thật** và cặp cắt
   thật để đối chiếu với 17250 / 7700 / 11100.
2. Dựng bảng thống kê từ khối `Piece` trong metadata; trước đó chốt với Owner nguồn khối lượng đơn vị
   (kg/m theo đường kính, hay khối lượng riêng thép trong khối `standard`).
   **Đã làm ở §41 — chọn khối lượng riêng, không dùng bảng tra.**
3. Bảng cắt phôi riêng cho xưởng từ `CutPairShortMm`/`CutPairLongMm`. **Đã làm ở §41.**
4. Nối `AbutmentConcreteCoverKernel` và `AbutmentBarSpacingKernel` vào planner (việc còn lại của §39).

## 41. Khối lượng, tham số thống kê nhìn thấy được, nội dung nhãn và hai bảng tự sinh (2026-08-13)

Mục này thay baseline test của §40 (**618 → 675**) và các con số `ruleVersion`/`ruleHash` của §40.
Preset hiện hành: `schemaVersion 2.3`, `ruleVersion 2026-08-13.7`,
`ruleHash 45851AB2B36D8E65CEFCF6CDADABBF8F3C888273150E44DF2E3EE5E1AAC6128A`;
**4/27 rule `enabled=true`** (`CVC-F1`, `CVC-F2`, `CVC-F3-T`, `CVC-F3-B`) — **không đổi**.
Đợt này **không chạm** một con số hình học nào của bốn lớp: chuỗi ga, cover, thứ tự lớp, đường kính,
bước, số thanh, nối chồng và cặp cắt đều giữ nguyên, và test
`TheFourLiveMats_KeepEveryGeometricNumberOfThePreviousWave` khoá đúng điều đó.

### 41.1 Khối lượng riêng vào khối `standard`; khối lượng được TÍNH, không tra bảng

`standard.steelDensityKgPerM3 = 7850` (`Models/AbutmentRebarPresetV1.cs`, `AbutmentStandardParametersSpec`
và `AbutmentResolvedStandardParameters`) là đầu vào cuối cùng còn thiếu của cột khối lượng theo §40.4.
Fail-closed bằng `ABUTMENT_STANDARD_STEEL_DENSITY_REQUIRED`; trường mới cũng vào phép kiểm "khai một
nửa" nên không thể khai thiếu.

`Domain/AbutmentBarMassKernel.cs` là kernel thuần: `TryUnitMassKgPerMetre` = khối lượng riêng ×
`AbutmentRebarMaterial.EffectiveAreaMm2` × 1e-6, tức **π d²/4**, **không** bảng tra kg/m. Lý do: bảng
chỉ trả lời cho những đường kính ai đó gõ vào nó, còn công thức đúng cho mọi đường kính rule khai
được; và diện tích lấy đúng đại lượng mà §5.11.2.1 dùng để neo thanh nên bảng khối lượng và chiều
dài neo không thể bất đồng về tiết diện. Dải chặn `[7000; 8500]` kg/m³ đủ hẹp để một giá trị viết
nhầm bằng g/cm³ (7,85) không lọt qua và làm nhẹ cả bảng đi một nghìn lần.

Kiểm chứng cả dải D12–D32 khớp giá trị danh nghĩa TCVN 1651 tới ba chữ số thập phân: 0,888 / 1,208 /
1,578 / 1,998 / **2,466** / 2,984 / 3,853 / 4,834 / **6,313**. Hai con số đậm là hai con số đầu bài
nêu và có test riêng.

### 41.2 Mười hai tham số nhìn thấy được, và ba điểm kỹ thuật

`Resources/DVB_Abutment_SharedParameters.txt` thêm 12 PARAM vào nhóm `DVB_ABUTMENT`, bind instance
vào Rebar như tám tham số cũ:

| Tham số | Kiểu | Phạm vi | Cột bảng |
|---|---|---|---|
| `DVB_BarPosition` | INTEGER | mỗi đoạn | truy vết cặp đoạn của một thanh |
| `DVB_PieceIndex` | INTEGER | mỗi đoạn | **bộ lọc** để bảng ra một dòng mỗi thanh hoàn thiện |
| `DVB_SpliceCount` | INTEGER | mỗi thanh | Số mối nối |
| `DVB_BendRadius` | LENGTH | mỗi thanh | Bán kính uốn |
| `DVB_BarLength` | LENGTH | mỗi thanh | Chiều dài một thanh |
| `DVB_BarTotalLength` | LENGTH | mỗi thanh | Tổng chiều dài (đã cộng nối) |
| `DVB_CutLength` | LENGTH | mỗi đoạn | chiều dài cắt — bảng cắt phôi |
| `DVB_LapLength` | LENGTH | mỗi thanh | Chiều dài mối nối |
| `DVB_UnitMass` | NUMBER | mỗi loại thanh | Khối lượng đơn vị kg/m |
| `DVB_Mass` | NUMBER | mỗi đoạn | khối lượng đoạn — bảng cắt phôi |
| `DVB_BarMass` | NUMBER | mỗi thanh | Khối lượng |
| `DVB_TagText` | TEXT | mỗi đoạn | Nội dung nhãn |

**Hai cặp chiều dài/khối lượng là cố ý, không phải trùng lặp.** `DVB_CutLength`/`DVB_Mass` mô tả
**đối tượng này** nên cộng đúng trên **mọi** đoạn; `DVB_BarTotalLength`/`DVB_BarMass` mô tả **cả
thanh** và được ghi **giống nhau lên mọi đoạn của thanh đó**, nên bảng lọc `DVB_PieceIndex = 1` vừa
ra một dòng mỗi thanh hoàn thiện vừa cộng đúng lượng thép. Một cặp duy nhất không làm được cả hai
việc: lọc thì thiếu một nửa thép, không lọc thì đếm 82 thanh.

**Điểm 1 — hàm ghi chỉ xử lý được chuỗi.** `AbutmentMetadataStore.WriteVisible` cũ kiểm
`StorageType.String` rồi mới `Set`. Đã tách thành `WriteVisibleText` (giữ nguyên từng dòng cho tám
tham số cũ), `WriteVisibleLengthMm`, `WriteVisibleNumber`, `WriteVisibleInteger`. `WriteVisibleLengthMm`
gọi `UnitUtils.ConvertToInternalUnits(..., UnitTypeId.Millimeters)` vì Revit lưu chiều dài bằng
**feet**: ghi thẳng số milimét thì bảng sẽ cộng ra hơn ba trăm lần lượng thép thật. Khối lượng cố ý
mang kiểu `NUMBER` (kg trần) chứ không phải quantity Mass của Revit, để con số hiện trên bảng đúng
bằng con số đã ghi bất kể project đặt hệ đơn vị nào; đơn vị nằm ở tiêu đề cột.

**Điểm 2 — thép cũ chưa có tham số mới.** Đã tách **hai tập** thay vì nối thêm vào tập cũ:

- `SharedParameterNames` (8 tham số truy nguyên) và `MissingVisibleParameters` **không đổi một chữ**.
  Đây là phép kiểm chấm mọi thanh managed, kể cả thanh do bản add-in cũ tạo; nối 12 tên mới vào đây
  là biến mọi thanh đó thành lỗi ngay ngày ship.
- `ScheduleParameterNames` (12 tham số mới) có phép kiểm riêng `MissingScheduleParameters`, và nó
  **chỉ** được hỏi ở hai chỗ: readback của **thanh vừa tạo trong lần chạy này** (`Readback` chỉ duyệt
  `created`, thanh cũ không nằm trong đó), và lệnh lập bảng — nơi câu trả lời đúng là **từ chối**.
- Phép kiểm mới chấm **giá trị**, không chấm sự hiện diện: binding theo category nên thanh cũ có
  tham số ngay khi bind xong và sẽ trả lời "có" trong khi rỗng. Bảng dựng trên đó trông như bảng
  thật nhưng tổng sai — tệ hơn không có bảng. Rỗng bị tính là thiếu. `DVB_BendRadius`, `DVB_LapLength`
  và `DVB_SpliceCount` chấm theo hiện diện vì 0 là giá trị thật của thanh thẳng không nối.
- Lệnh lập bảng gặp thanh cũ thì dừng bằng `ABUTMENT_SCHEDULE_LEGACY_REBAR`, nêu số thanh, id và
  tham số thiếu, và bảo người dùng chạy **Tạo lại vùng** để nâng cấp — chứ không lặng lẽ bỏ qua.
- Thêm cổng `AbutmentSharedParameterBinder`: nếu file shared parameter đang chạy thiếu tên nào trong
  `AbutmentMetadataStore.ExpectedSharedParameterNames` thì báo thẳng đường dẫn file và danh sách
  thiếu. Bản deploy cũ để lại bản copy cạnh DLL và bản copy đó **thắng** bản nhúng; không có cổng
  này thì một file cũ sẽ hiện ra dưới dạng mọi thanh mới đều fail readback mà không nói vì sao.

**Điểm 3 — tám tham số cũ giữ nguyên tuyệt đối.** Cùng GUID, cùng tên, cùng `TEXT`, cùng
`USERMODIFIABLE 0`, cùng giá trị ghi. Test `SharedParameterFile_KeepsTheEightLineageParametersUntouched`
khoá từng dòng. Mười hai tham số mới để `USERMODIFIABLE 1` — **khác** tám tham số cũ, có chủ đích:
shared parameter không user-modifiable có thể bị Revit trả `IsReadOnly=true` và `WriteVisible*` sẽ
lặng lẽ bỏ qua, mà một cột bảng rỗng im lặng là đúng kiểu hỏng đợt này không được phép có.

### 41.3 Nội dung nhãn: hai quyết định, `Domain/AbutmentRebarTagKernel.cs`

Dạng nhãn: `{số hiệu} {số thanh}Ø{đường kính}a{bước}`.

**Hai lớp ngang cùng mark bản vẽ F3.** `TryResolveMarkLabels` nhận cả bộ lớp của plan: mark bản vẽ
chỉ một lớp mang thì dùng **nguyên**; mark do nhiều lớp cùng mang thì thêm hậu tố lấy từ **chính
canonicalMark** của lớp đó (phần sau dấu gạch cuối). Ra `F3-T` và `F3-B`. Không bịa ký hiệu mới:
`T`/`B` đã có sẵn trong preset và đã được ghi lên từng thanh ở `DVB_CanonicalMark`, nên nhãn truy
ngược được về **cả hai** mark; và mark bản vẽ vẫn là thứ đầu tiên đọc thấy. Hai lớp mà hậu tố vẫn
trùng thì fail-closed `ABUTMENT_TAG_MARK_INDISTINGUISHABLE` chứ không cho hai thảm đọc như một.

**Thanh dọc hai đoạn.** Con số trên nhãn là **số vị trí thanh** (41, 31), **không bao giờ** là số
đối tượng (82, 62): bản vẽ ghi 41 và bảng đã chốt liệt kê theo thanh hoàn thiện. Đoạn thứ nhất mang
**đúng chuỗi bản vẽ cần**; đoạn thứ hai lặp lại chuỗi đó kèm dấu tiếp nối `[2/2]`. Nhờ vậy mỗi thanh
chỉ có **một** nhãn sạch để đặt lên bản vẽ, và người vẽ lỡ tag nhầm nửa sau thì **nhìn thấy ngay**
trên tờ giấy thay vì âm thầm nhân đôi số thanh.

**Bước ghi trên nhãn là bước ghi trên bản vẽ**, tức `spacingMm` = 150 cho cả bốn lớp — kể cả hai lớp
dọc mà bước vuông góc thanh chỉ 136 mm ở góc 65°. Lý do: nhãn nằm cạnh chính chuỗi kích thước mà đội
trắc đạc định vị theo, ghi 136 là mâu thuẫn với đường kích thước bên cạnh. Con số 136 là đại lượng
**thi công** của phép kiểm khoảng hở §5.10.3.1.1 (§37.5), không phải chú thích bản vẽ; nó vẫn được
tính từ hình học thật và **không** ghi thành tham số.

Bốn chuỗi nhãn thật: `F1 31Ø20a150` · `F2 41Ø32a150` · `F3-T 105Ø20a150` · `F3-B 105Ø20a150`; đoạn
hai của hai lớp dọc: `F1 31Ø20a150 [2/2]` và `F2 41Ø32a150 [2/2]`.

### 41.4 Bảng thống kê: nhóm theo mark NỘI BỘ

`Domain/AbutmentRebarScheduleKernel.cs` (kernel thuần) nhóm theo `InternalMark` = `canonicalMark`,
**không** theo Schedule Mark. Đây là toàn bộ cái bẫy: hai thảm ngang đều mang mark bản vẽ `F3`, nhóm
theo mark bản vẽ thì 210 thanh của hai cao độ khác nhau gộp thành một dòng.

`RevitServices/AbutmentScheduleService.CreateFinishedBarSchedule` dựng `ViewSchedule` cho hạng mục
Rebar, sort/group theo `DVB_CanonicalMark`, `IsItemized=false`, lọc `DVB_AssemblyId = <mã bộ thép>`
**và** `DVB_PieceIndex = 1`. Cột: Số hiệu · Mark bản vẽ · Đường kính · Bán kính uốn · Số lượng
(Count) · Chiều dài 1 thanh · Số mối nối · Chiều dài mối nối · Tổng chiều dài (Totals) · KL đơn vị ·
Khối lượng (Totals). Hai cột Totals dùng cặp "mỗi thanh" của §41.2 nên cộng đúng dưới bộ lọc.

Bảng liệt kê **theo thanh hoàn thiện kèm cột mối nối** đúng quyết định Owner: lớp dọc ra **41 dòng
thanh 17250 mm với 1 mối nối 1550 mm**, không phải 82 đoạn.

Cả hai lệnh đọc **từ chính Rebar trong model** (`AbutmentScheduleService.TryReadManagedBars` đọc
tham số trên element), **không** đọc lại preset. Mã từ chối: `ABUTMENT_SCHEDULE_LEGACY_REBAR`,
`_NO_MANAGED_REBAR`, `_ASSEMBLY_AMBIGUOUS`, `_HOST_UNDECLARED`, `_BAR_TYPE_UNRESOLVED`, và của kernel
`ABUTMENT_SCHEDULE_` `ROWS_EMPTY` / `INTERNAL_MARK_REQUIRED` / `ROW_INVALID` / `LAP_REQUIRED` /
`GROUP_INCONSISTENT` / `PIECE_SET_INCOMPLETE` / `LENGTH_NOT_RECONCILED` / `MASS_UNRESOLVED`,
`ABUTMENT_CUTTING_LIST_MASS_UNRESOLVED`, `ABUTMENT_MASS_` `BAR_DIAMETER_INVALID` /
`STEEL_DENSITY_INVALID` / `UNIT_MASS_INVALID` / `LENGTH_INVALID`, `ABUTMENT_TAG_` `LAYERS_UNDECLARED` /
`RULE_ID_REQUIRED` / `DRAWING_MARK_REQUIRED` / `RULE_DUPLICATE` / `CANONICAL_MARK_REQUIRED` /
`MARK_INDISTINGUISHABLE` / `REQUEST_UNDECLARED` / `MARK_LABEL_REQUIRED` / `BAR_COUNT_INVALID` /
`BAR_DIAMETER_INVALID` / `PITCH_INVALID` / `PIECE_INVALID`, `ABUTMENT_SCHEDULE_FACTS_UNRESOLVED`
(factory, chặn **trước** transaction) và `ABUTMENT_READBACK_SCHEDULE_METADATA`.

`LENGTH_NOT_RECONCILED` là cổng đối chiếu chéo giữa hai bảng: tổng chiều dài đoạn cắt phải bằng
`số thanh × (chiều dài thanh + số nối × chiều dài nối)`, nếu không thì hai bảng đang nói hai khối
lượng khác nhau.

### 41.5 Bảng cắt phôi cho xưởng

`TryBuildCuttingList` nhóm theo (mark nội bộ, chiều dài cắt) nên lớp dọc ra **hai dòng mỗi mark** —
đúng hai chiều dài cắt thật — còn lớp ngang ra một dòng. `ExportCuttingList` ghi file **CSV UTF-8 có
BOM** vào `%LocalAppData%\DVB_ADDIN\Exports\Abutment\`, dùng **dấu phân cách danh sách và định dạng
số của chính máy** nên Excel Việt (phân cách `;`, thập phân `,`) mở ra đúng cột thay vì dồn một cột.
Kernel mặc định vẫn là TAB + InvariantCulture để test khoá được chuỗi.

### 41.6 Cổng đã siết

`ABUTMENT_RULE_STANDARD_PARAMETERS_INCOMPLETE` **mở rộng** sang **mọi rule đang bật**, không chỉ rule
cần tính neo/nối: mọi thanh được tạo giờ đều mang một khối lượng lên bảng, nên rule không tra được
khối tham số tiêu chuẩn phải dừng ở Analyze chứ không để người dùng phát hiện lúc Create. Với preset
hiện hành không đổi gì vì khối `standard` ở cấp preset giải được cho cả 27 rule.

### Bằng chứng offline

- `dotnet test` Release: **675/675 pass**, 0 fail (baseline trước đó 618/618).
- Build `Release.R25` với `DeployAddin=false`, `LaunchRevit=false`: **0 warning / 0 error**;
  `Release.R26`: 14 warning `CS0618` API Rebar cũ đã biết trong `RebarApiAdapter.cs` / 0 error;
  `Release.R27`: **0 warning / 0 error**.
- Preset source, bản copy trong output R25 và bản nhúng trong DLL cùng SHA-256
  `A0274C8E937C60B49B0220B5BB6E86C79D7A697149AF7C44C0465985B78F4B74`. File shared parameter cả ba
  bản cùng SHA-256 `BD056877A72198B652F594D7E49FD4C00E07D84EFC47EBA1D63D0D3AA14BFF78`.
- `Validate()` trên preset **đúng như đóng gói**: không `ABUTMENT_RULE_HASH_MISMATCH`, không issue Error.
- Repo này **không phải git repository**. Bản sao lưu trước khi sửa:
  `Resources/Presets/cau-van-cui-m2.v1.json.20260813T154658.bak`
  (SHA-256 `6EBD09430FB6498DE588EFC5779E71026F63F3830F0291AC24C49180C6D2949B`).
- Chưa deploy, chưa reload add-in, chưa chạm Revit hay model nào.

### 41.7 Người dùng cần làm gì trong Revit

1. **Tạo lại thép trước khi lập bảng.** Thép tạo bởi bản add-in cũ chưa có 12 tham số mới; lệnh lập
   bảng sẽ **từ chối** và nêu tên thanh. Chạy **Rải Thép Mố Cầu → Tạo lại vùng** cho từng khu vực.
2. **Bảng thống kê:** ribbon `ĐVB_ADDIN` → **Bảng Thống Kê Thép Mố** → chọn mố. Add-in tạo sẵn một
   ViewSchedule đã nhóm, đã lọc, đã đủ cột và mở luôn. Không phải cấu hình gì.
3. **Bảng cắt phôi:** ribbon → **Bảng Cắt Phôi Thép Mố** → chọn mố. File `.csv` được ghi và đường dẫn
   hiện trong hộp thoại; mở bằng Excel.
4. **Family nhãn** (một lần cho cả dự án): New → Family → chọn template
   `Metric Structural Rebar Tag.rft` (hoặc `Metric Generic Annotation.rft` nếu không có). Trong family:
   Create → **Label** → đặt vào vị trí muốn hiện chữ → trong hộp Edit Label bấm nút **Add Parameter**
   (biểu tượng thư mục) → **Shared parameter → Select** → trỏ tới file
   `Resources\DVB_Abutment_SharedParameters.txt` cạnh DLL → nhóm `DVB_ABUTMENT` → chọn
   **`DVB_TagText`** → OK → đưa sang cột phải của Edit Label → OK. Đặt cỡ chữ, lưu thành
   `DVB_Nhan_Thep.rfa`, Load into Project. Sau đó dùng **Annotate → Tag by Category** bấm vào thanh
   thép; nhãn tự hiện `F3-T 105Ø20a150`.
   Với hai lớp dọc, tag vào **đoạn thứ nhất**; nếu nhãn hiện `[2/2]` là đang tag nửa sau, dời tag sang
   đoạn kia.
5. Add-in **không** tự đặt nhãn, không tạo kích thước, không tạo mặt cắt, không tạo khung tên — đúng
   phạm vi đợt này.

### 41.8 Residual đã biết, không được lặng lẽ bỏ

- **Chưa Create lần nào trên Revit thật**, nên 12 tham số mới, hai lệnh bảng và family nhãn mới chỉ
  có bằng chứng offline. Riêng phần `ViewSchedule` (tên trường schedulable, `ScheduleFilter`,
  `ScheduleFieldDisplayType.Totals`) **không** kiểm được ngoài Revit; lệnh đã viết để **suy giảm có
  báo** thay vì ném lỗi khi Revit không cấp một trường built-in nào đó.
- **Chiều dài thanh lớp F3 (~6250 mm) là giả thiết fixture**, không phải số đo. Số thật chỉ có sau
  Create đầu tiên; §40.8 đã ghi cùng giới hạn này cho cặp cắt 7700/11100.
- **`USERMODIFIABLE 1`** cho 12 tham số mới nghĩa là người dùng sửa tay được. Cổng đồng nhất theo mark
  của kernel bắt được phần lớn kiểu sửa bậy, nhưng không phải mọi kiểu.
- Bán kính uốn hiện lấy từ `hookSpec.bendRadiusMm` của rule; bốn lớp bệ đều `kind=None` nên bằng 0.
  Thanh có móc sau này cần nối `AbutmentBarBendKernel` — **vẫn chưa nối**, đúng như §40.4 ghi.
- Năm kernel §37 vẫn chưa nối vào planner cho cover và khoảng hở (§39 bước 4 còn mở).
- Tồn đọng đầu thanh F3 của §39.6 **giữ nguyên**, Owner đã chấp nhận.

## 42. Tách lệch dày PROFILE/solid khỏi gate mặt bằng 8 mm (2026-08-14)

Analyze sau `FootingTopResolve.R25.20260814.1` đã chốt mặt trên bệ (0 mm, dày đo 1800 mm) nhưng vẫn
khóa zone móng bằng **một** lỗi `ABUTMENT_PROFILE_FOOTING_INVALID`: *"Profile-to-solid mismatch
200 mm vượt gate 8 mm"*. 200 mm đúng bằng hiệu số bản vẽ 2000 − family 1800. Gate 8 mm ở
`TryResolveProfileFooting` đang lấy `max(lệch mặt bằng, lệch cao độ)` rồi fail-closed cả hai.

Quyết định (không nới gate 8 mm):

- Lệch **mặt bằng / path** trên 8 mm vẫn `ABUTMENT_PROFILE_FOOTING_INVALID`, khóa Create.
- Lệch **chiều dày** PROFILE so với solid đo được → `ABUTMENT_PROFILE_FOOTING_DEPTH_MISMATCH`
  (Warning), zone móng **vẫn khóa được**. Path lấy biên PROFILE; cao độ lớp lấy mặt top/bottom
  solid đã resolve. Không im lặng thay PROFILE bằng solid.
- `ABUTMENT_CORE_PROFILE_TOPOLOGY_UNRESOLVED` (chữ ký backwall return/corbel) **vẫn Warning** —
  khóa Stem/Backwall, không khóa 4 lớp móng. Không sửa adapter cánh/thân trong đợt này.
- Không hardcode 1800 / 0 / 200. Family khác dày khác vẫn đi cùng một kernel.

Kernel thuần: `Domain/AbutmentProfileSolidGateKernel.cs`. Extractor gọi kernel rồi mới quyết
Error/Warning. Test `AbutmentProfileSolidGateKernelTests` khóa đúng case screenshot, case lệch
ngang 200 mm, và case khớp trong 8 mm.

Bản deploy: `ProfileSolidGate.R25.20260814.1`. Không revert FootingTopResolve.

### Bước tiếp theo

1. Deploy R25 side-by-side rồi smoke bốn lớp bệ trên bản copy SAFE: Analyze **khóa được zone Móng mố**,
   bốn mark hiện 31 / 41 / 105 / 105, Create → readback → receipt, rồi chạy hai lệnh bảng và đối chiếu
   **282 thanh / 354 đoạn / ~9.472 kg** với bảng khối lượng của hồ sơ.
2. Đọc receipt lấy chiều dài thanh **đo thật** của cả bốn lớp và cập nhật con số ~6250 mm của F3.
3. Nối `AbutmentBarBendKernel` vào cột bán kính uốn trước khi mở bất kỳ rule nào có móc.
4. Nối `AbutmentConcreteCoverKernel` và `AbutmentBarSpacingKernel` vào planner (việc còn lại của §39).
