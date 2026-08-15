# Kịch bản nghiệm thu bốn lớp thép bệ mố trên Revit thật

Tài liệu này dành cho **người vận hành** (anh Mèo Cọc), không dành cho lập trình viên. Mỗi bước ghi rõ
bấm gì, con số phải thấy, và dấu hiệu nào là hỏng.

Nguồn số liệu: `AGENT.md` §33–§41 và `Resources/Presets/cau-van-cui-m2.v1.json`
(`ruleVersion 2026-08-13.8`, `ruleHash 669DE40AE26CF74267E5883B618A5778BBCFD993C839D69928C86939F667AAEA`).
Mọi thông báo lỗi trích trong tài liệu là **chuỗi thật trong mã nguồn**, không phải diễn giải.

---

## 0. Đọc trước, 60 giây

Add-in hiện có bản `ProfileSolidGate.R25.20260814.1`. Bản `FootingTopResolve.R25.20260814.1` trước đó
đã sửa `ABUTMENT_FOOTING_TOP_UNRESOLVED` (mặt trên bệ ở cao độ 0, dày đo 1800 mm) — **không revert**.
Bản này tách lệch chiều dày PROFILE 2000 mm / solid 1800 mm ra khỏi gate mặt bằng 8 mm: lệch dày là
**cảnh báo có số**, không khóa zone móng; lệch ngang trên 8 mm vẫn fail-closed. **Bốn lớp thép bệ chưa
Create lần nào trên Revit thật** — lần chạy này vẫn là lần đầu tiên.

Mục tiêu quan trọng nhất **không phải** là "tạo được thép". Mục tiêu là **lấy số đo thật** để thay bốn
con số đang là giả thiết: chiều dài thanh dọc 17250 mm, cặp chiều dài cắt 7700 + 11100 (D32) và
7200 + 10700 (D20), và chiều dài thanh ngang ~6250 mm. Bốn con số đó quyết định cột khối lượng
~9.472 kg của cả bộ thép.

| | |
|---|---|
| Thời gian | khoảng **60–90 phút**, trong đó bước chuẩn bị model chiếm nửa đầu |
| Số bước | **8** bước bắt buộc + 1 bước ghi kết quả |
| Kết quả cần mang về | biên lai JSON, file CSV bảng cắt phôi, 3–5 ảnh mặt cắt, và bảng đối chiếu ở §9 đã điền |
| Rủi ro cao nhất | hai thảm thép **tiếp xúc nhau, không còn dư địa** (xem §10 mục 1) |

Nếu chỉ đọc được một dòng: **làm trên bản copy, và đừng chỉnh cover dù chỉ 1 mm.**

---

## 1. Bước 1 — Chuẩn bị an toàn

### Làm gì

1. Đóng hết Revit đang mở model gốc. Kiểm tra không còn tiến trình `Revit.exe` nào đang giữ file mố.
2. Copy file model gốc sang một thư mục làm việc riêng, **không** copy đè lên bất kỳ file nào có sẵn.
3. Đặt tên bản copy theo mẫu:

```text
SAFE_MO_CAU_VAN_CUI_M2_<yyyyMMdd>_<HHmm>.rvt
```

Ví dụ: `SAFE_MO_CAU_VAN_CUI_M2_20260813_1620.rvt`.

4. Mở **bản copy**, xác nhận đúng tên file trên thanh tiêu đề Revit trước khi làm bất cứ việc gì khác.

### Vì sao đặt tên như vậy

- Tiền tố `SAFE_` là quy ước đã dùng suốt các phiên trước (`SAFE_TEMP_REBAR.rvt`, `SAFE_MO_CAU_LBH.rfa`
  trong `tmp/review-workspace/`). Nhìn tên là biết ngay file nào được phép ghi.
- Có dấu thời gian nên chạy lại lần hai không ghi đè bằng chứng của lần một. Khi số liệu hai lần chạy
  khác nhau, còn đủ hai file để đối chiếu.
- Đây là lần Create đầu tiên trên Revit thật của bốn lớp bệ. Rollback của add-in đã có và đã kiểm
  (`TransactionGroup` cuộn lại toàn bộ khi readback fail), nhưng **rollback bảo vệ được model, không
  bảo vệ được file**. Bản copy mới là lớp bảo vệ cuối.

### Dấu hiệu hỏng

| Dấu hiệu | Nghĩa là | Làm gì |
|---|---|---|
| Thanh tiêu đề Revit vẫn hiện tên file gốc | đang mở nhầm file | Đóng ngay, **không** bấm Save, mở lại bản copy |
| Revit hỏi "file đang được người khác dùng" | còn tiến trình cũ giữ file | Đóng hết Revit, kiểm Task Manager, copy lại |
| Bản copy mở ra bị mất link / mất family | copy thiếu file phụ thuộc | Copy cả thư mục dự án, không copy lẻ một file `.rvt` |

---

## 2. Bước 2 — Chuẩn bị model

Sáu điều kiện. Thiếu bất kỳ điều nào thì Create bị **khóa cứng**, không phải cảnh báo. Kiểm hết sáu
điều **trước khi** bấm Phân tích sẽ tiết kiệm được vài vòng thử.

### 2.1 Loại thép: phải có `RebarBarType` D20 **và** D32

Bốn lớp dùng: F1 = D20, F2 = D32, F3-T = D20, F3-B = D20.

**Cách kiểm trong Revit:** `Manage → Project Standards`… hoặc nhanh hơn: mở Project Browser →
`Families → Structural Rebar → Rebar Bar` và xem danh sách type. Tên khớp chính xác `D20`, `D32` là
tốt nhất; add-in cũng chấp nhận type có đường kính đo được bằng 20 / 32 mm (sai số ±0,55 mm) dù tên
khác.

**Thiếu thì thấy gì:**

- Không có type nào: `REBAR_BAR_TYPE_CATALOG_EMPTY` —
  *"Model chưa có RebarBarType. Hãy tạo/load type D12/D16/D20/D25/D32 trước khi rải thép mố."*
- Có type nhưng không khớp: `REBAR_BAR_TYPE_NOT_FOUND` —
  *"Không tìm thấy RebarBarType khớp 'D32' (D32). Type hiện có trong model: …"*
- Type đúng tên nhưng sai đường kính: *"Type 'D32' có đường kính D25 nhưng rule cần D32."*

### 2.2 Vật liệu và Structural Asset: **đây là chỗ hay vấp nhất**

Mỗi `RebarBarType` phải trỏ tới một Material, và Material đó phải có **Structural Asset** đọc được
`Minimum Yield Stress ≥ 400 MPa` (mác CB400-V).

**Cách kiểm trong Revit:** chọn type `D20` → `Edit Type` → tham số `Material` → bấm nút `…` mở
Material Browser → tab **Physical** → phải có một asset gắn vào, và trong đó ô
`Minimum Yield Stress` phải có số (≥ 400 MPa). Làm lại đúng như vậy cho `D32`.

Chỉ gõ số `fy` vào một material generic là **không đủ**: add-in đọc `MinimumYieldStress` của Structural
Asset, không đọc tên material.

**Thiếu thì thấy gì** (hai chốt, hai mã lỗi, cùng nội dung):

- Lúc Phân tích — `REBAR_BAR_TYPE_GRADE_METADATA_MISSING`:
  *"RebarBarType 'D32' chưa có Material/Structural Asset nên không đọc được fy; rule yêu cầu mác
  CB400-V với fy ≥ 400 MPa. Gán Structural Asset cho type này trong Revit rồi Phân tích lại."*
- Lúc Tạo thép — `ABUTMENT_REBAR_GRADE_METADATA_MISSING`: *"CVC-F2: RebarBarType 'D32' không có
  Material/Structural Asset nên không đọc được fy…"*

Đây là **Error**, không phải Warning — đã siết ở §33.3 và §38.5 mục 2, không được nới.

> Mẹo từ phiên 2026-08-11 (§29): trên bản SAFE cũ, hai type `32M` và `D16` đã được gán **cùng
> material đã kiểm chứng** của `D20` (`TCVN 1651-2008`). Đây là cách nhanh nhất: tìm material nào đã
> có asset đạt, gán lại cho các type còn thiếu.

### 2.3 Family mố: phải bật `Can Host Rebar`

**Cách kiểm trong Revit:** chọn khối mố → `Edit Family` → `Family Types` hoặc
`Create → Properties → Family Category and Parameters` → tick **Can host rebar**. Family cũng phải có
`Structural Material` là bê tông. Sau khi sửa: `Load into Project → Overwrite the existing version`.

Add-in kiểm bằng `RebarHostData.IsValidHost`, **không** phải `GetRebarHostData != null` — hai thứ này
khác nhau và chính chỗ đó từng làm Analyze xanh nhưng Create văng lỗi (§21).

**Thiếu thì thấy gì:** `ABUTMENT_REBAR_HOST_INVALID` —
*"Host 'MO CAU - LBH' (1234567) không thể tạo Rebar. Đây là lỗi family/project, không phải thiếu
RebarShape: bật Can Host Rebar, gán Structural Material bê tông/precast trong family mố, reload family
vào project, rồi Phân tích lại."*

### 2.4 Ba mặt cắt PROFILE: `PROFILE_TM` L / Center / R phải đọc được

Toàn bộ chuỗi ga, trục đo, biên bệ và cao độ bốn lớp đều lấy từ ba nested family có tên chứa
`PROFILE_TM`. Preset đang đặt `requireProfileMassMarkers = true`, nên thiếu là **Error**.

**Cách kiểm trong Revit:** chọn khối mố → `Edit Family`, xem trong family có đủ ba instance nested
`PROFILE_TM` (trái / giữa / phải) và cả ba đều là loop phẳng, khép kín. Ở mức project, sau khi Phân
tích, panel thông báo phải có dòng Info `ABUTMENT_PROFILE_MASS_RESOLVED` — *"Đã nhận diện 3 profile
station section(s): …"*.

**Thiếu thì thấy gì:**

- `ABUTMENT_PROFILE_MASS_MISSING` — *"Thiếu ba nested PROFILE_TM station sections L/Center/R; Create
  fail-closed."*
- `ABUTMENT_PROFILE_FOOTING_REQUIRED` — *"stationLayout chỉ đối chiếu được khi biên bệ đã được đo từ
  các section PROFILE_TM; trục đo không được lấy từ dấu trục family."*

### 2.5 Bệ mố phải là **một khối liền** trong vùng thảm

Nếu hình học host cắt một vị trí thanh thành hai mẩu rời, engine từ chối chứ không tự nối bừa.

**Cách kiểm:** nhìn khối bệ ở 3D — không có khe, không có hai solid rời chồng nhau trong phạm vi bệ.

**Thiếu thì thấy gì:** `ABUTMENT_MAT_STATION_FRAGMENTED` — *"CVC-F2 có station bị host cắt thành 2
đoạn rời; sơ đồ nối chồng chỉ giải được cho station liền một mạch."*

### 2.6 Chỉ dẫn kỹ thuật: **tỷ lệ nước–xi măng ≤ 0,40**

Đây không phải việc trong Revit mà là việc trong hồ sơ, nhưng **bắt buộc** phải có trước khi đổ bê
tông lớp mặt trên. Cover mặt trên 70 mm của lớp F3-T chỉ **đạt** khi tỷ lệ nước–xi măng ≤ 0,40
(75 × 0,8 = 60 mm ≤ 70 mm). Ở tỷ lệ 0,50 thì yêu cầu thành 90 mm và 70 mm **vi phạm ngay**.

Ràng buộc này đã ghi trong preset tại `footingCover.engineerApprovalReference` của F1 và F3-T
(`OWNER-DECISION-2026-08-13-F1-CENTROID-100-WC-RATIO-MAX-040`). Hiện **không có guard nào trong
runtime chặn việc này** — nó chỉ tồn tại dưới dạng khai báo (§38.5 mục 3). Trách nhiệm là của người
lập chỉ dẫn kỹ thuật.

### Bảng tổng hợp điều kiện tiên quyết

| # | Điều kiện | Kiểm ở đâu | Mã lỗi nếu thiếu |
|---|---|---|---|
| 1 | Có `RebarBarType` D20 và D32 | Project Browser → Structural Rebar | `REBAR_BAR_TYPE_CATALOG_EMPTY` / `REBAR_BAR_TYPE_NOT_FOUND` |
| 2 | Cả hai type có Structural Asset, fy ≥ 400 MPa | Edit Type → Material → Physical | `REBAR_BAR_TYPE_GRADE_METADATA_MISSING` / `ABUTMENT_REBAR_GRADE_METADATA_MISSING` |
| 3 | Family mố bật `Can Host Rebar` + có Structural Material | Edit Family → Category and Parameters | `ABUTMENT_REBAR_HOST_INVALID` |
| 4 | Ba `PROFILE_TM` L/Center/R đọc được | Edit Family; panel Info sau Phân tích | `ABUTMENT_PROFILE_MASS_MISSING` / `ABUTMENT_PROFILE_FOOTING_REQUIRED` |
| 5 | Bệ liền khối trong vùng thảm | Nhìn 3D | `ABUTMENT_MAT_STATION_FRAGMENTED` |
| 6 | Chỉ dẫn kỹ thuật ràng buộc w/c ≤ 0,40 | Hồ sơ, ngoài Revit | *không có guard runtime* |

**Không cần `RebarShape`.** Cả bốn rule dùng `CurveDrivenFreeForm`; cổng `REBAR_SHAPE_CATALOG_EMPTY`
chỉ áp cho rule `ShapeDriven` đang bật, mà hiện không có rule nào như vậy.

---

## 3. Bước 3 — Deploy bản add-in mới

Nguyên tắc: **deploy side-by-side**, không ghi đè lên bản đang chạy. Đây là quy ước đã dùng từ §26 và
lặp lại ở §27–§32.

### Làm gì

1. Đóng Revit. Nếu đang mở model chưa lưu thì lưu hoặc bỏ thay đổi trước — **không** ghi đè DLL của
   một tiến trình đang chạy.
2. Đóng gói bằng script có sẵn của repo (đây là script duy nhất trong `tools/` làm việc này):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Publish-RevitPackage.ps1" `
    -RevitVersion 2025 `
    -OutputDirectory "$env:APPDATA\Autodesk\Revit\Addins\2025\BIM.DatViet.ProfileSolidGate.R25.20260814.1\BIM.DatViet"
```

   Cách đã dùng trong repo (không hardcode path tiếng Việt): từ thư mục add-in chạy

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tmp\publish-and-deploy-r25.ps1"
```

   Script bắt buộc thư mục đích **chưa tồn tại** (để artifact của target Revit khác không lẫn vào), tự
   kiểm phiên bản `Nice3point.Revit.Toolkit`/`Extensions` khớp Revit 2025, và in ra SHA-256 của
   `BIM.DatViet.dll`. **Ghi lại chuỗi SHA-256 đó.**

3. Sửa manifest `%APPDATA%\Autodesk\Revit\Addins\2025\BIM.DatViet.addin` để node `<Assembly>` trỏ vào
   thư mục mới (script deploy đã làm bước này):

```xml
<Assembly>BIM.DatViet.ProfileSolidGate.R25.20260814.1\BIM.DatViet\BIM.DatViet.dll</Assembly>
```

   Sao lưu manifest cũ sang `backups\` trước khi sửa — thư mục `backups\` đã có sẵn cho đúng việc này.

4. Mở lại Revit. Ribbon được dựng trong `Application.OnStartup()` nên **bắt buộc khởi động lại**, không
   có cách reload nóng.

### Kết quả mong đợi

Tab `ĐVB_ADDIN`, panel `Commands` có **bốn** nút:

1. `Rải Thép Hố Ga`
2. `Rải Thép Mố Cầu`
3. `Bảng Thống Kê Thép Mố`
4. `Bảng Cắt Phôi Thép Mố`

Thấy đủ bốn nút là bản mới đã nạp. Thấy hai nút là Revit vẫn đang chạy bản cũ.

### Dấu hiệu hỏng

| Dấu hiệu | Nghĩa là | Làm gì |
|---|---|---|
| Ribbon chỉ có 2 nút | manifest chưa trỏ đúng, hoặc Revit chưa restart thật | Kiểm lại `<Assembly>`, đóng hẳn Revit rồi mở lại |
| Hộp thoại *"Preset deployed 'cau-van-cui-m2.v1.json' khác với preset embedded trong DLL"* | thư mục deploy trộn file của hai lần build | Xóa thư mục, chạy lại script với `-OutputDirectory` mới |
| Hộp thoại nêu *"Shared parameter file '…' thiếu N tham số"* | file `DVB_Abutment_SharedParameters.txt` cạnh DLL là bản cũ | Deploy lại trọn bộ; bản copy cạnh DLL **thắng** bản nhúng |
| Script báo *"Output directory must be new"* | thư mục đích đã tồn tại | Đổi hậu tố ngày/giờ trong tên thư mục |

---

## 4. Bước 4 — Chạy Phân tích

### Làm gì

Ribbon `ĐVB_ADDIN → Rải Thép Mố Cầu` → chọn khối mố khi Revit yêu cầu → cửa sổ tool mở ra và **tự chạy
Phân tích**. Nếu cần chạy lại, bấm nút **`Phân tích hình học`**.

### Con số phải thấy, và ý nghĩa từng con số

Khu vực Móng mố có **9 mark** trong hồ sơ; đợt này **4 mark mở**, 5 mark còn lại (`A4`, `A6`, `F1`,
`F2`, `F3` — tức F4 đến F7 trên bản vẽ) vẫn khóa và hiện dòng "Mark khóa: …" kèm lý do. **Đó là
bình thường.** Bốn mark đang mở nằm trên cùng danh sách và mỗi mark hiện một dòng số lượng:

| Mark hiển thị | Số lượng phải thấy | Nghĩa |
|---|---|---|
| `Canonical A1 ← Source F1` | **31 thanh, 62 đoạn** | 31 vị trí thanh trên bản vẽ, mỗi vị trí 2 đoạn vì có mối nối |
| `Canonical A2 ← Source F2` | **41 thanh, 82 đoạn** | như trên, lớp D32 dưới |
| `Canonical A3-T ← Source F3` | **105 thanh** | không nối, một đoạn một thanh |
| `Canonical A3-B ← Source F3` | **105 thanh** | như trên |

Cộng lại: **282 thanh bản vẽ / 354 đối tượng Rebar**. Con số 354 là thứ phải khớp với biên lai ở bước 6.

Chỗ khác cần liếc:

- Dòng tổng lớp: `9 mark cấu hình • 4 mở • 5 khóa`. Thấy `0 mở` là preset sai bản.
- Panel thông báo **phải có** dòng Info `ABUTMENT_FOOTING_TOP_RESOLVED` — mặt trên bệ ở cao độ **0 mm**,
  cách đáy **1800 mm**. Thấy lại `ABUTMENT_FOOTING_TOP_UNRESOLVED` là đang chạy bản diagnostic cũ
  (target +200 mm), đóng Revit rồi nạp lại `ProfileSolidGate.R25.20260814.1`.
- **Phải khóa được** canonical zone Móng mố. Header không còn dòng đỏ
  `Chưa khóa được canonical zone Móng mố`. Bốn mark F1 / F2 / F3-T / F3-B hiện số lượng
  31 / 41 / 105 / 105.
- Được phép thấy **cảnh báo** `ABUTMENT_PROFILE_FOOTING_DEPTH_MISMATCH` — PROFILE vẽ dày 2000 mm,
  solid dày 1800 mm, lệch 200 mm. Đó là lưu ý có số, **không** phải lỗi khóa zone. Thấy lại
  `ABUTMENT_PROFILE_FOOTING_INVALID` với câu "mismatch 200 mm vượt gate 8 mm" là đang chạy bản cũ.
- Cảnh báo cánh/thân (`ABUTMENT_WING_PROFILE_*`, `ABUTMENT_DRAWING_RULE_DISABLED`,
  `ABUTMENT_CORE_PROFILE_TOPOLOGY_UNRESOLVED`) **là bình thường** — chúng khóa F4–F7 / cánh / thân,
  **không** chặn bốn lớp móng.
- Panel thông báo có một dòng **Info** `ABUTMENT_PILE_PLACEMENT_MARGIN` — **đây là bình thường**, không
  phải lỗi. Nó xuất hiện đúng vì lớp F1 dùng `pileObstaclePolicy=Reject` và preset có margin 30 mm
  (§40.6).
- Dòng Info `ABUTMENT_PROFILE_MASS_RESOLVED` xác nhận đọc được 3 section.

### Nếu thấy con số khác

| Thấy | Nghĩa là | Làm gì |
|---|---|---|
| `ABUTMENT_FOOTING_TOP_UNRESOLVED` | đang chạy bản cũ (target = đáy + 2000 = +200) | Đóng Revit, nạp `ProfileSolidGate.R25.20260814.1` |
| `ABUTMENT_PROFILE_FOOTING_INVALID` + "mismatch 200 mm vượt gate 8 mm" | đang chạy bản gộp lệch dày vào gate mặt bằng | Đóng Revit, nạp `ProfileSolidGate.R25.20260814.1` |
| `Chưa khóa được canonical zone Móng mố` | zone móng chưa bind — nếu chỉ còn cảnh báo dày 200 mm thì bản cũ | Đóng Revit, nạp bản mới, Phân tích lại |
| **0 thanh** ở mọi lớp | thiếu `RebarBarType` hoặc thiếu Structural Asset | Đọc dòng lỗi `REBAR_BAR_TYPE_*`, quay lại §2.1/§2.2 |
| F1 ra **29** thay vì 31 | vùng chừa vệt chân thân mố bị khai rộng thành dải 2360→4190 | Đây là lỗi dữ liệu preset, **dừng lại**, báo lại — biên vùng chừa là đoạn đóng (§38.1) |
| F1 ra **39** hoặc F2 ra **39** | đang chạy preset cũ trước §38 | Bản deploy sai, quay lại bước 3 |
| F3 ra **105** nhưng lưới không phủ hết chiều dài | lỗi chiếu xiên hai lần đã sửa ở §32 quay lại | Kiểm `ruleHash`, quay lại bước 3 |
| Số thanh đúng nhưng có Error `ABUTMENT_MAT_LAYER_CLASH` | hai thảm cùng mặt bị chồng tim | **Dừng**, xem §10 mục 1 — không được tự nới cover |
| `ABUTMENT_ENABLED_RULES_EMPTY` | preset đang khóa toàn bộ rule | Bản deploy là preset cũ (trước §38), quay lại bước 3 |

> **Không chỉnh gì trên UI.** Ô cover và toggle bật/tắt lớp đang bị khóa theo hồ sơ; nếu cố đổi, tool
> trả về đúng câu: *"Chỉ được đổi RebarBarType (đường kính và mác thép). Enabled, spacing, cover, layer
> và khoảng lùi đang khóa theo hồ sơ."*

---

## 5. Bước 5 — Chạy Tạo thép

### Cả vùng một lần, **không có lựa chọn từng lớp**

Câu trả lời dứt khoát: **bấm một lần cho cả khu vực Móng mố**, tạo cả bốn lớp trong một thao tác.
Không phải vì tiện, mà vì hiện không có đường nào khác:

1. **UI không cho tắt bớt lớp.** Toggle của mỗi mark bị khóa theo preset. Cố tắt một lớp rồi Áp dụng
   thì tool ném đúng câu *"Chỉ được đổi RebarBarType… Enabled, spacing, cover, layer và khoảng lùi đang
   khóa theo hồ sơ."*
2. **Create là thao tác cấp khu vực, không phải cấp lớp.** Bấm `Tạo thép mới` lần thứ hai trên cùng khu
   vực bị chặn bằng `ABUTMENT_CREATE_ALREADY_EXISTS` — *"Khu vực đã có Rebar do DVB quản lý; hãy dùng
   Tạo lại vùng."* Còn `Tạo lại khu vực` thì **xóa sạch rồi dựng lại toàn bộ** khu vực, chứ không thêm
   một lớp vào bên cạnh.
3. **Tạo từng lớp còn nguy hiểm hơn.** Phép kiểm tách lớp `ValidateFootingLayerSeparation` chỉ so được
   khi mỗi mặt bê tông có đủ hai thảm. Tạo một lớp một lần thì phép kiểm quan trọng nhất của đợt này
   **không bao giờ chạy**.
4. **Hỏng thì không để lại rác.** Toàn bộ nằm trong một `TransactionGroup`; readback fail là cuộn lại
   sạch và biên lai ghi `ABUTMENT_CREATE_ROLLED_BACK`.

### Làm gì

1. Xác nhận khu vực đang chọn là **Móng mố**.
2. Bấm **`Tạo thép mới`**. Chờ — 354 đối tượng cộng readback mất một lúc, Revit có thể trắng màn hình
   vài chục giây. **Không bấm gì thêm.**

Nếu model đã có thép do add-in tạo từ trước (ví dụ 262 thanh của phiên §29), nút đúng là
**`Tạo lại khu vực`** chứ không phải `Tạo thép mới`.

### Phải kiểm ngay sau khi tạo

| Kiểm | Đạt là |
|---|---|
| Tiêu đề tool | chuyển sang trạng thái "Móng mố đã có Rebar do DVB quản lý"; nút `Tạo lại khu vực` sáng lên |
| Panel thông báo | **không** còn dòng Error nào |
| Chọn tất cả Rebar trong model | đếm được **354** đối tượng thuộc mố này |
| Undo | **chỉ có đúng một** mục undo tên `DVB Abutment Reinforcement <mã>` — chứng tỏ cả 354 thanh nằm trong một nhóm giao dịch |

Nút **`Hoàn tác`** trong tool **không** xóa thép: nó chỉ hoàn tác chỉnh sửa preset. Muốn bỏ thép vừa
tạo thì dùng `Ctrl+Z` của Revit.

### Dấu hiệu hỏng

| Mã lỗi trong biên lai | Nghĩa | Làm gì |
|---|---|---|
| `ABUTMENT_CREATE_ROLLED_BACK` | tạo xong nhưng readback không đạt, đã cuộn lại sạch | Model nguyên trạng. Đọc câu lỗi kèm theo để biết tầng nào fail |
| `ABUTMENT_CREATE_ROLLBACK_UNCONFIRMED` | **nghiêm trọng** — không xác nhận được cuộn lại | **Không** save. Đóng Revit không lưu, dùng lại bản copy sạch |
| `ABUTMENT_READBACK_STATION_COUNT` | số vị trí đọc lại khác kế hoạch: *"CVC-F2: plan yêu cầu 41 vị trí thanh, readback có 40 vị trí trên 80 Rebar."* | Ghi lại nguyên văn, đây là phát hiện quan trọng |
| `ABUTMENT_READBACK_BAR_PIECE` | hai đoạn của một thanh chồng nhau chưa đủ chiều dài nối | Ghi lại; liên quan trực tiếp tới cặp cắt thật |
| `ABUTMENT_SHARED_PARAMETER_BIND_FAILED` | không gắn được 20 tham số vào category Rebar | Xem §3, file shared parameter cạnh DLL là bản cũ |
| `ABUTMENT_MAT_LAYER_CLASH` | hai thảm cùng mặt chồng tim | Xem §10 mục 1 |

---

## 6. Bước 6 — Đọc biên lai

### Biên lai nằm ở đâu

```text
%LocalAppData%\DVB_ADDIN\Receipts\Abutment\<yyyyMMddTHHmmssZ>-<mã lần chạy>.json
```

Dán đường dẫn `%LocalAppData%\DVB_ADDIN\Receipts\Abutment` vào thanh địa chỉ Windows Explorer, sắp xếp
theo thời gian, lấy file mới nhất. Mở bằng Notepad hoặc trình duyệt.

Cách nhanh hơn — chạy script đối chiếu tự động, nó tự tìm biên lai mới nhất:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Compare-AbutmentReceipt.ps1"
```

### Những trường cần đọc

| Trường | Giá trị phải thấy | Ý nghĩa |
|---|---|---|
| `Succeeded` | `true` | Create đã commit. `false` là đã cuộn lại |
| `Operation` | `Create` (hoặc `RebuildZone`) | thao tác nào đã chạy |
| `RuleHash` | `669DE40A…F667AAEA` | **chứng minh đang chạy đúng preset `2026-08-13.8`**. Khác chuỗi này là bản deploy sai, mọi số sau đó vô nghĩa |
| `GeometryHash`, `PlanHash` | chuỗi bất kỳ | dùng để truy vết lần chạy sau |
| `CreatedIds` | **354** phần tử | tổng đối tượng Rebar |
| `CreatedItems[].Key` | xem dưới | **trường quan trọng nhất** |
| `Issues` | không có `Severity: Error` | mọi cảnh báo còn lại đọc kỹ nhưng không chặn |
| `ReceiptPersisted` | `true` | biên lai đã ghi được xuống đĩa |

`CreatedItems[].Key` mang cả mã rule, số thứ tự vị trí thanh và số thứ tự đoạn:

```text
CVC-F1-001-P1   lớp F1, vị trí thanh 1, đoạn 1
CVC-F1-001-P2   lớp F1, vị trí thanh 1, đoạn 2   ← cùng một thanh với dòng trên
CVC-F3-T-001    lớp F3-T, vị trí thanh 1, không có hậu tố vì lớp này không nối
```

Đếm theo mã rule phải ra:

| Rule | Vị trí thanh | Đối tượng |
|---|---|---|
| `CVC-F1` | 31 | 62 |
| `CVC-F2` | 41 | 82 |
| `CVC-F3-T` | 105 | 105 |
| `CVC-F3-B` | 105 | 105 |
| **Tổng** | **282** | **354** |

### Điều phải biết trước khi mở file: **biên lai KHÔNG chứa chiều dài thanh**

Đây là phát hiện quan trọng của lần soạn kịch bản này. Biên lai JSON hiện chỉ ghi 12 trường định danh
cộng bốn danh sách (`CreatedIds`, `CreatedItems`, `RemovedIds`, `Issues`). **Không có** chiều dài thanh
đo thật, **không có** cặp chiều dài cắt, **không có** khối lượng.

Chiều dài thật đang được ghi ở ba nơi khác:

1. **Tham số nhìn thấy được trên từng thanh** — `DVB_CutLength` (chiều dài đoạn), `DVB_BarLength`
   (chiều dài thanh hoàn thiện), `DVB_BarTotalLength`, `DVB_LapLength`, `DVB_BarMass`. Chọn một thanh
   trong Revit, mở Properties, kéo xuống nhóm `Identity Data` là thấy.
2. **File CSV bảng cắt phôi** — cách lấy số **nhanh và chính xác nhất**, xem bước 7.
3. Metadata truy nguyên trong `SourceEvidenceJson` của từng thanh (khối `Piece`) — chỉ đọc được bằng
   công cụ lập trình.

Vì vậy **quy trình bắt buộc là: Create → biên lai → chạy bước 7 để lấy CSV → mới có đủ số đo thật.**
Script `Compare-AbutmentReceipt.ps1` đọc cả hai file và ghép lại thành một bảng.

### Dấu hiệu hỏng

| Dấu hiệu | Nghĩa | Làm gì |
|---|---|---|
| Không có file nào trong thư mục Receipts | Create chưa chạy hoặc không ghi được đĩa | Kiểm `ReceiptWriteError` trên UI |
| `RuleHash` khác `669DE40A…` | đang chạy preset cũ | Hủy kết quả, làm lại từ bước 3 |
| `CreatedIds` có 354 phần tử nhưng `CreatedItems` ít hơn | biên lai bị cắt | Ghi lại, đây là lỗi cần sửa |
| Có Key dạng `CVC-F2-001` (không hậu tố `-P`) cho lớp F2 | mối nối **không** được dựng, F2 ra thanh liền 17,25 m | **Dừng ngay**, thanh 17,25 m không thi công được |

---

## 7. Bước 7 — Chạy hai lệnh bảng

### Làm gì

1. Ribbon `ĐVB_ADDIN → Bảng Thống Kê Thép Mố` → chọn khối mố.
   Add-in tự tạo một ViewSchedule đã nhóm, đã lọc, đã đủ cột và **mở luôn**. Trước đó hiện một hộp
   thoại tóm tắt — chụp lại hộp thoại này.
2. Ribbon `ĐVB_ADDIN → Bảng Cắt Phôi Thép Mố` → chọn khối mố.
   Hộp thoại hiện danh sách dòng cắt và **đường dẫn file CSV**. Chép đường dẫn đó.
   File nằm ở `%LocalAppData%\DVB_ADDIN\Exports\Abutment\<yyyyMMdd-HHmmss>-ABT-<id>-cat-phoi.csv`,
   mở bằng Excel.

### Số phải khớp

**Bảng thống kê** liệt kê theo **thanh hoàn thiện**, lọc `DVB_PieceIndex = 1`, nhóm theo mark nội bộ:

| Số hiệu | Mark bản vẽ | Ø | Số lượng | Chiều dài 1 thanh | Số mối nối | Chiều dài mối nối |
|---|---|---|---|---|---|---|
| `A1` | F1 | 20 | 31 | ~17250 mm *(chờ đo)* | 1 | 650 mm |
| `A2` | F2 | 32 | 41 | ~17250 mm *(chờ đo)* | 1 | 1550 mm |
| `A3-T` | F3 | 20 | 105 | ~6250 mm *(chờ đo)* | 0 | 0 |
| `A3-B` | F3 | 20 | 105 | ~6250 mm *(chờ đo)* | 0 | 0 |

Tổng **282 thanh**, tổng khối lượng **~9.472 kg**.

**Bảng cắt phôi** liệt kê theo **đoạn cắt thật**, nhóm theo (mark, chiều dài cắt) — nên hai lớp dọc ra
**hai dòng mỗi mark**, hai lớp ngang ra một dòng:

| Số hiệu | Chiều dài cắt | Số đoạn |
|---|---|---|
| `A1` | ~7200 mm *(chờ đo)* | 31 |
| `A1` | ~10700 mm *(chờ đo)* | 31 |
| `A2` | ~7700 mm *(chờ đo)* | 41 |
| `A2` | ~11100 mm *(chờ đo)* | 41 |
| `A3-T` | ~6250 mm *(chờ đo)* | 105 |
| `A3-B` | ~6250 mm *(chờ đo)* | 105 |

Tổng **354 đoạn**.

### Đối chiếu chéo giữa hai bảng

Ba phép cộng phải khớp, và add-in đã tự kiểm bằng cổng `ABUTMENT_SCHEDULE_LENGTH_NOT_RECONCILED`:

1. **Số lượng:** 282 thanh (bảng thống kê) ↔ 354 đoạn (bảng cắt phôi). Chênh lệch đúng bằng
   31 + 41 = 72, tức số thanh có mối nối.
2. **Chiều dài:** với mỗi mark, `tổng chiều dài đoạn cắt = số thanh × (chiều dài thanh + số nối × chiều
   dài nối)`. Ví dụ A2: `41 × (17250 + 1550) = 41 × 18800 = 770.800 mm`, và cũng bằng
   `41 × 7700 + 41 × 11100`. Đây là chỗ đọc ra **chiều dài thanh đo thật**: lấy hai chiều dài cắt trên
   bảng cắt phôi cộng lại rồi trừ chiều dài nối.
3. **Khối lượng:** tổng khối lượng hai bảng phải bằng nhau tới từng kilôgam. Khối lượng đơn vị tính từ
   công thức `ρ × πd²/4`, không tra bảng: D20 = 2,466 kg/m, D32 = 6,313 kg/m.

Script `Compare-AbutmentReceipt.ps1` làm cả ba phép này tự động.

### Dấu hiệu hỏng

| Dấu hiệu | Nghĩa | Làm gì |
|---|---|---|
| `ABUTMENT_SCHEDULE_LEGACY_REBAR` — *"…N/M thanh thép của mố '…' được tạo bởi bản add-in trước khi có tham số thống kê…"* | model còn thép cũ chưa có 12 tham số mới | Chạy `Tạo lại khu vực` cho khu vực đó rồi lập bảng lại |
| `ABUTMENT_SCHEDULE_ASSEMBLY_AMBIGUOUS` | thép của mố mang nhiều mã bộ thép | Xóa thép cũ không thuộc lần chạy này |
| `ABUTMENT_SCHEDULE_LENGTH_NOT_RECONCILED` | hai bảng đang nói hai khối lượng khác nhau | **Dừng**, ghi lại — cột khối lượng không tin được |
| Hộp thoại có mục "Lưu ý: Revit không cho đưa tham số '…' vào bảng" | Revit không cấp một trường vào schedule | Không chặn, nhưng bảng thiếu cột đó — ghi lại |
| Bảng thống kê ra **82 dòng** cho F2 thay vì **41** | bộ lọc `DVB_PieceIndex = 1` không hoạt động | Ghi lại, đây là lỗi cần sửa |
| Excel mở CSV dồn hết vào một cột | dấu phân cách của máy khác với lúc xuất | Dùng `Data → Text to Columns`, chọn dấu `;` |

---

## 8. Bước 8 — Kiểm bằng mắt trong model

Năm phép kiểm mà mắt người làm tốt hơn máy. Mỗi phép kiểm chụp lại một ảnh.

### 8.1 Thứ tự chồng lớp, nhìn trên mặt cắt ngang

Cắt một mặt cắt ngang qua bệ (vuông góc trục cầu), đặt Detail Level = Fine.

Từ **mặt trên** đi xuống phải thấy: **F3-T trước (thanh ngang, tim 80 mm)**, rồi F1 (thanh dọc, tim
100 mm). Từ **đáy** đi lên: **F3-B trước (tim 134 mm)**, rồi F2 (tim 160 mm).

Nói cách khác: **hai lớp ngang F3 nằm ngoài cùng ở cả hai mặt, hai lớp dọc F1/F2 nằm phía trong.** Đây
là điều §38.2 đã đảo lại so với preset cũ; nếu thấy ngược thì đang chạy preset trước §38.

Đo bằng Measure trong Revit: khoảng cách tim–tim giữa F3-T và F1 phải đúng **20 mm** (= (20+20)/2), giữa
F3-B và F2 phải đúng **26 mm** (= (20+32)/2). Hai thảm **chạm nhau, không cắt nhau**.

### 8.2 Khoảng trống ở vùng thân mố của lớp trên

Nhìn từ trên xuống (view mặt bằng, chỉ bật lớp F1). Phải thấy một **dải trống rộng 1830 mm** chạy suốt
chiều dài bệ, ngay tại vệt chân thân mố. Hai thanh sống gần nhất nằm ở hai bên dải trống đó.

Lớp F2 ở dưới **không** có dải trống — nó chạy liên tục đủ 41 thanh.

Đếm nhanh: nửa phía gót có 16 thanh, nửa phía mũi có 15 thanh, cộng 31.

Nếu dải trống chỉ rộng khoảng 1545 mm (đúng bằng bề dày thân) hoặc rộng hơn 2000 mm thì vùng chừa bị
khai sai — dừng lại và ghi lại.

### 8.3 Hai đoạn nối chồng nhau

Chọn **một thanh** của lớp F2 bằng cách bấm vào nó rồi `Tab` để duyệt: phải thấy **hai** đối tượng
Rebar riêng biệt, không phải một.

- Chọn đoạn thứ nhất → Properties → `DVB_PieceIndex = 1`, `DVB_CutLength` ≈ 7700 mm.
- Chọn đoạn thứ hai → `DVB_PieceIndex = 2`, `DVB_CutLength` ≈ 11100 mm.
- Vùng chồng nhau của hai đoạn phải dài **1550 mm**.

Rồi nhìn **thanh kế bên**: mối nối phải nằm ở **đầu ngược lại**. Thanh lẻ lắp đoạn ngắn trước, thanh
chẵn đảo đầu — nên trên bất kỳ mặt cắt nào cũng không quá 50% số thanh đang nối. Nhìn từ trên xuống,
các mối nối phải tạo thành **hai hàng so le**, không dồn thành một hàng.

Nếu mọi mối nối nằm trên cùng một mặt cắt: dừng lại, đây là vi phạm điều khoản nối chồng.

### 8.4 Lưới ngang phải phủ hết chiều dài bệ

Nhìn mặt bằng, chỉ bật lớp F3-T. Lưới 105 thanh phải phủ **suốt** chiều dài bệ 15800 mm, thanh đầu
cách mặt đầu bệ 100 mm và thanh cuối ở ga 15700.

Đây là chỗ đã hỏng một lần (§32): đầu xa từng hụt khoảng 1571 mm vì lỗi chiếu xiên hai lần. Nếu thấy
mảng trống ở một đầu bệ thì lỗi đó quay lại — ghi lại ngay, kèm số đo phần hụt.

### 8.5 Thép nằm trong bê tông và né cọc đúng cách

- Xoay 3D, bật Section Box quanh bệ: **không thanh nào chọc ra ngoài mặt bê tông**.
- Đầu thanh F3 cách mặt xiên khoảng **85 mm** đo vuông góc. Bản vẽ ghi 99,7 mm nên mỗi đầu **dài hơn
  bản vẽ ~15 mm** — đây là **tồn đọng Owner đã chấp nhận** (§39.6), **không** phải lỗi mới, và **không
  được tự sửa**.
- Lớp F2 và F3-B chạy **xuyên qua** vị trí cọc (đúng cấu tạo, `ContinueThroughMonolithicHost`); lớp F1
  ở mặt trên né cọc theo chính sách `Reject`. Không thanh nào cắm vào bao cọc ở lớp F1.

---

## 9. Bảng đối chiếu số liệu

Cột **Nguồn** là cột quan trọng nhất: nó nói con số nào đã chắc và con số nào còn là giả thiết.

Ba loại nguồn:

- **BẢN VẼ** — đọc thẳng từ hồ sơ P38, không suy diễn.
- **TÍNH** — tính ra từ tiêu chuẩn TCVN 11823-5:2017 hoặc từ hình học đo được.
- **GIẢ THIẾT** — chưa có số đo thật; **chính là thứ lần chạy này phải thay**.

### 9.1 Số lượng

| Đại lượng | Giá trị kỳ vọng | Nguồn |
|---|---|---|
| Số vị trí thanh F1 (D20 dọc, mặt trên) | 31 | **BẢN VẼ** — chuỗi 41 ga trừ 10 ga trong vệt chân thân mố |
| Số đối tượng Rebar F1 | 62 | **TÍNH** — 31 × 2 đoạn |
| Số vị trí thanh F2 (D32 dọc, mặt dưới) | 41 | **BẢN VẼ** — mặt bằng D-D / mặt cắt E-E |
| Số đối tượng Rebar F2 | 82 | **TÍNH** — 41 × 2 đoạn |
| Số vị trí thanh F3-T (D20 ngang, mặt trên) | 105 | **BẢN VẼ** — `100 + 51@150` mỗi nửa |
| Số vị trí thanh F3-B (D20 ngang, mặt dưới) | 105 | **BẢN VẼ** |
| **Tổng vị trí thanh** | **282** | **TÍNH** — 31 + 41 + 105 + 105 |
| **Tổng đối tượng Rebar** | **354** | **TÍNH** — 62 + 82 + 105 + 105 |

### 9.2 Chuỗi ga và hình học

| Đại lượng | Giá trị kỳ vọng | Nguồn |
|---|---|---|
| Chuỗi ga F1/F2 (đo từ mép gót) | `110 + 15@150 + 229 + 170 + 164 + 7@150 + 217 + 14@150 + 110 = 6400` | **BẢN VẼ** — riêng mắt 164 mm không có nhãn, xác định bằng phép đóng chuỗi, đo được 163,9 / 163,8 |
| Chuỗi ga F3-T/F3-B | `100 + 104@150`, ga cuối 15700 trên 15800 | **BẢN VẼ** |
| Vùng chừa F1 | `[2538; 4083]` đo từ mép gót, loại đúng 10 ga | **BẢN VẼ** — bằng `heelWidthMm` và `heelWidthMm + stemBaseThicknessMm` |
| Dải trống nhìn thấy của F1 | 1830 mm | **TÍNH** — hệ quả: khoảng giữa thanh sống ở 2360 và 4190 |
| Bề rộng bệ theo trục đo | 6400 mm (vuông góc thật 5800) | **BẢN VẼ** |
| Chiều dài bệ theo trục dọc | 15800 mm | **BẢN VẼ** |
| Góc chéo | 65° (thanh dọc nghiêng 25°) | **BẢN VẼ** |
| Vai gót / bề dày thân / vai mũi | 2538 + 1545 + 2317 = 6400 | **BẢN VẼ** — đã sửa đổi chỗ toe/heel ở §34.2 |

### 9.3 Cao độ lớp và lớp bảo vệ

| Đại lượng | Giá trị kỳ vọng | Nguồn |
|---|---|---|
| Tim F3-T dưới mặt trên | 80 mm | **TÍNH** — suy từ tim F1; đo trên bản vẽ được 76 |
| Tim F1 dưới mặt trên | 100 mm | **BẢN VẼ** — kích thước ghi thẳng P38 |
| Tim F2 trên đáy | 160 mm | **BẢN VẼ** — kích thước ghi thẳng P38 |
| Tim F3-B trên đáy | 134 mm | **TÍNH** — suy từ tim F2; đo trên bản vẽ được 149 |
| Cover mặt F3-T / F1 / F2 / F3-B | 70 / 90 / 144 / 124 mm | **TÍNH** — hệ quả của tim thanh, bản vẽ ghi tim chứ không ghi cover |
| Cover bốn cạnh (cả bốn lớp) | 75 mm | **BẢN VẼ** |
| `layerOrder` F3-T / F3-B | 0 (ngoài cùng) | **BẢN VẼ** |
| `layerOrder` F1 / F2 | 1 (phía trong) | **BẢN VẼ** |
| Tách tim F3-T ↔ F1 | 20 mm = (20+20)/2 | **TÍNH** — **tiếp xúc, không còn dư địa** |
| Tách tim F3-B ↔ F2 | 26 mm = (20+32)/2 | **TÍNH** — **tiếp xúc, không còn dư địa** |
| Đầu thanh F3 cách mặt xiên | 85 mm vuông góc (bản vẽ ghi 99,694) | **TÍNH** — tồn đọng Owner đã chấp nhận, §39.6 |

### 9.4 Vật liệu và tiêu chuẩn

| Đại lượng | Giá trị kỳ vọng | Nguồn |
|---|---|---|
| Mác bê tông `f'c` | 30 MPa | **BẢN VẼ** (biên dưới) — hồ sơ mâu thuẫn với C35, cố ý lấy biên dưới cho neo/nối dài hơn |
| Mác thép | CB400-V, fy = 400 MPa | **BẢN VẼ** — trang 6 |
| Cấp phơi nhiễm | ven biển / đúc áp đất → cover cơ bản 75 mm | **TÍNH** — TCVN 11823-5 §5.12.3 |
| Tỷ lệ nước–xi măng | ≤ 0,40 | **BẢN VẼ (ràng buộc bắt buộc)** — quyết định Owner, phải ghi vào chỉ dẫn kỹ thuật |
| Cỡ hạt cốt liệu lớn nhất | 20 mm | **BẢN VẼ** |
| Chiều dài phôi kho | 11700 mm | **BẢN VẼ** — nhà cung cấp |
| Khối lượng riêng thép | 7850 kg/m³ | **BẢN VẼ** — thép cacbon kết cấu |
| Khối lượng đơn vị D20 | 2,466 kg/m | **TÍNH** — `7850 × πd²/4`, khớp TCVN 1651 |
| Khối lượng đơn vị D32 | 6,313 kg/m | **TÍNH** — như trên |

### 9.5 Neo và mối nối

| Đại lượng | Giá trị kỳ vọng | Nguồn |
|---|---|---|
| Chiều dài neo ℓ_d D20 | 480 mm | **TÍNH** — §5.11.2.1, bước 10 mm |
| Chiều dài neo ℓ_d D32 | 1180 mm | **TÍNH** — §5.11.2.1, bước 10 mm |
| Cấp nối | Cấp B (không quá 50% thanh nối/mặt cắt, không có hồ sơ chứng minh tỷ số 2) | **TÍNH** — §5.11.5.3.1 |
| Nối chồng D20 | 650 mm | **TÍNH** — 480 × 1,3 = 624 → 630 (bước 10) → 650 (bước chi tiết 50) |
| Nối chồng D32 | 1550 mm | **TÍNH** — trùng khít bộ số kỹ sư đã chốt |
| Số mối nối mỗi thanh F1/F2 | 1 | **TÍNH** — 17250 > 11700 |
| Số mối nối mỗi thanh F3 | 0 | **TÍNH** — ~6250 < 11700 |
| Tỷ lệ nối trên mặt cắt bất lợi nhất | 0,5 | **TÍNH** |

### 9.6 Bốn con số **GIẢ THIẾT** — mục tiêu chính của lần chạy này

| Đại lượng | Giá trị đang giả định | Nguồn |
|---|---|---|
| **Chiều dài thanh dọc F1/F2** | **17250 mm** | **GIẢ THIẾT chờ đo** — giá trị danh nghĩa; planner đo chiều dài thật từ biên PROFILE lúc lập kế hoạch |
| **Cặp chiều dài cắt F2 (D32)** | **7700 + 11100**, hở 1850 | **GIẢ THIẾT chờ đo** — suy từ 17250 + nối 1550 |
| **Cặp chiều dài cắt F1 (D20)** | **7200 + 10700**, hở 2850 | **GIẢ THIẾT chờ đo** — suy từ 17250 + nối 650 |
| **Chiều dài thanh ngang F3** | **~6250 mm** | **GIẢ THIẾT (fixture)** — chưa từng đo, §41.8 ghi rõ |

Hệ quả kéo theo, cũng là giả thiết:

| Đại lượng | Giá trị đang giả định | Nguồn |
|---|---|---|
| Khối lượng F1 | ~1.368 kg | **GIẢ THIẾT** — 31 × 17900 mm × 2,466 kg/m |
| Khối lượng F2 | ~4.866 kg | **GIẢ THIẾT** — 41 × 18800 mm × 6,313 kg/m |
| Khối lượng F3-T | ~1.618 kg | **GIẢ THIẾT** — 105 × 6250 mm × 2,466 kg/m |
| Khối lượng F3-B | ~1.618 kg | **GIẢ THIẾT** |
| **Tổng khối lượng bốn lớp** | **~9.472 kg** | **GIẢ THIẾT** — đúng chỉ khi bốn chiều dài trên đúng |

**Cách tính lại từ số đo thật** khi có bảng cắt phôi:

```text
chiều dài thanh = chiều dài cắt ngắn + chiều dài cắt dài − chiều dài nối
```

Ví dụ F2: nếu bảng cắt phôi cho 7700 và 11100 thì thanh = 7700 + 11100 − 1550 = 17250 mm, đúng giả
thiết. Nếu cho 7800 và 11200 thì thanh = 17450 mm, phải cập nhật cả cụm số ở §9.6 và tính lại khối
lượng.

Lưu ý: nếu chiều dài thật khác 17250 mm thì **cặp cắt cũng khác**, và đoạn dài chỉ rơi đúng bội số
100 mm khi `chiều dài thanh + chiều dài nối` chia hết cho 100. Kernel không ép điều đó; nó chỉ bảo đảm
đoạn ngắn nằm trên module và cả hai đoạn nằm trong phôi kho 11700 mm.

---

## 10. Danh sách rủi ro đã biết

Xếp theo mức nguy hiểm giảm dần.

### 1. Hai thảm thép tiếp xúc nhau — **không còn dư địa nào** ⚠ cao nhất

Cả hai mặt bê tông đều có hai thảm đặt **sát nhau đúng bằng tổng hai bán kính**: mặt trên
`100 − 80 = 20 = (20+20)/2`, mặt dưới `160 − 134 = 26 = (20+32)/2`. Nghĩa là hai thảm **chạm nhau,
không cắt nhau**, và **dịch một lớp đi 1 mm là cổng `ABUTMENT_MAT_LAYER_CLASH` nổ ngay**. Test
`BothMatsOfEachFace_ArePairedAndTheOverlapCheckStillFires` khóa đúng tính chất này.

Hệ quả: **tuyệt đối không chỉnh ô cover trong tool**, dù chỉ một milimét, dù nhìn thấy có vẻ "thoáng
hơn thì đẹp hơn". Bốn con số cover 70 / 90 / 144 / 124 mm thuộc **một bộ nhất quán** đã kiểm bằng số
học hai chiều (§38.2). Đổi một con số là phá cả bộ.

Nếu gặp `ABUTMENT_MAT_LAYER_CLASH`: ghi lại nguyên văn hai tên rule, khoảng cách đo được và khoảng cách
tối thiểu, rồi **dừng**. Đây là dữ liệu chẩn đoán, không phải việc để tự chỉnh.

### 2. Phần tạo bảng trong Revit **chưa từng chạy ngoài môi trường test**

`ViewSchedule`, tên trường schedulable, `ScheduleFilter` và `ScheduleFieldDisplayType.Totals` **không
kiểm được ngoài Revit** (§41.8). Lệnh đã viết để **suy giảm có báo** thay vì ném lỗi: nếu Revit không
cấp một trường built-in nào đó, hộp thoại hiện dòng *"Revit không cho đưa tham số '…' vào bảng"* và
bảng vẫn tạo nhưng thiếu cột.

Đọc kỹ mục "Lưu ý" ở cuối hộp thoại bảng thống kê. Bảng thiếu cột mà không ai để ý còn tệ hơn bảng
không tạo được.

### 3. Thép do bản add-in cũ tạo sẽ làm lệnh lập bảng **từ chối**

12 tham số thống kê mới chỉ được ghi lên thanh tạo bởi bản này. Thanh cũ (ví dụ 262 thanh của phiên
§29) có tham số nhưng **rỗng**, và phép kiểm chấm **giá trị** chứ không chấm sự hiện diện — vì bảng
dựng trên tham số rỗng trông như bảng thật nhưng tổng sai.

Lệnh lập bảng dừng bằng `ABUTMENT_SCHEDULE_LEGACY_REBAR`, nêu số thanh, id và tham số thiếu. Cách xử
lý: chạy **`Tạo lại khu vực`** cho từng khu vực còn thép cũ. Đừng xóa tay từng thanh.

### 4. Cover mặt trên 70 mm phụ thuộc ràng buộc tỷ lệ nước–xi măng

70 mm chỉ **đạt** khi w/c ≤ 0,40 (75 × 0,8 = 60 ≤ 70). Ở w/c = 0,50, yêu cầu thành 90 mm và 70 mm **vi
phạm ngay**. Ràng buộc này **phải được ghi vào chỉ dẫn kỹ thuật**.

Hiện **không có guard nào trong runtime** chặn việc đổ bê tông sai tỷ lệ — năm kernel tính theo tiêu
chuẩn (§37) vẫn **chưa được nối vào planner**. Nó chỉ là một chuỗi khai báo trong preset.

### 5. `expectedBarCount` mang nghĩa **số vị trí thanh**, không phải số đối tượng

Đây là chỗ dễ đọc nhầm nhất khi đối chiếu số liệu. Bản vẽ đếm 41 thanh, model chứa 82 đối tượng, và
**cả hai đều đúng**. Nhãn ghi 41, bảng thống kê ghi 41 dòng, bảng cắt phôi ghi 82 đoạn, biên lai ghi
82 `CreatedItems`.

Ai đó đối chiếu "41 với 82" rồi kết luận sai gấp đôi là hiểu nhầm, không phải lỗi.

### 6. Bật lớp F1 thay đổi hành vi tìm cọc

`CVC-F1` dùng `pileObstaclePolicy=Reject`, nên bật nó **kích hoạt** `PilePlacementMargin`: planner cộng
thêm 30 mm vào clear cọc và ghi issue Info `ABUTMENT_PILE_PLACEMENT_MARGIN`; bộ trích hình học cũng nới
bán kính tìm cọc vì đường kính lớn nhất trong các rule đang bật đã lên D32.

**Dòng Info này là bình thường**, không phải regression. Nhưng nó nghĩa là kết quả lần chạy này khác
mọi lần chạy trước — đừng so ảnh với ảnh cũ rồi kết luận vội.

### 7. Bán kính uốn hiện luôn bằng 0

Cột "Bán kính uốn" trên bảng thống kê lấy từ `hookSpec.bendRadiusMm` của rule. Bốn lớp bệ đều
`kind=None` nên cột này **bằng 0 và đúng bằng 0**. `AbutmentBarBendKernel` **chưa được nối**; thanh có
móc sau này sẽ ra số sai nếu ai đó bật một rule có móc trước khi nối kernel.

### 8. 12 tham số mới để `USERMODIFIABLE 1` — người dùng sửa tay được

Khác với 8 tham số truy nguyên cũ (khóa `USERMODIFIABLE 0`). Cố ý như vậy vì tham số không
user-modifiable có thể bị Revit trả `IsReadOnly=true` và hàm ghi sẽ **lặng lẽ bỏ qua** — một cột bảng
rỗng im lặng còn tệ hơn.

Cổng đồng nhất theo mark bắt được phần lớn kiểu sửa bậy, **nhưng không phải mọi kiểu**. Đừng sửa tay
các tham số `DVB_*` trên thanh thép.

### 9. Bản copy shared parameter cạnh DLL **thắng** bản nhúng trong DLL

Deploy thiếu file `Resources\DVB_Abutment_SharedParameters.txt` cùng phiên bản với DLL sẽ hiện ra dưới
dạng **mọi thanh mới đều fail readback mà không nói vì sao**. Cổng
`AbutmentSharedParameterBinder` đã được thêm để báo thẳng đường dẫn file và danh sách tham số thiếu —
nhưng chỉ khi deploy đúng cách bằng script ở bước 3.

### 10. Cột khối lượng đúng chỉ khi bốn chiều dài giả thiết đúng

Con số ~9.472 kg là **kết quả tính từ giả thiết**, không phải số đo. Nó sẽ thay đổi ngay khi có chiều
dài thật. Đừng đưa con số này vào hồ sơ dự toán trước khi hoàn tất bước 7.

---

## 11. Ghi lại kết quả về đâu

Mang về bốn thứ:

1. **File biên lai JSON** — copy từ `%LocalAppData%\DVB_ADDIN\Receipts\Abutment\`.
2. **File CSV bảng cắt phôi** — copy từ `%LocalAppData%\DVB_ADDIN\Exports\Abutment\`.
3. **Bản in kết quả script:**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Compare-AbutmentReceipt.ps1" | Tee-Object doi-chieu.txt
```

Script tự tìm biên lai và bảng cắt phôi mới nhất. Muốn chỉ định file cụ thể thì truyền
`-ReceiptPath` và `-CuttingListPath`. Mã thoát: **0** = mọi thứ khớp, **1** = có chỗ lệch hoặc có
lỗi chặn, **2** = không đọc được đầu vào.

Script đối chiếu được: định danh lần chạy, `RuleHash` so với preset trong repo, số vị trí thanh và
số đối tượng của từng lớp, tổng 282 / 354, danh sách vấn đề — tất cả **từ biên lai**; cộng cặp chiều
dài cắt, chiều dài thanh hoàn thiện, số đoạn và khối lượng — **từ bảng cắt phôi**. Mọi con số kỳ
vọng lấy thẳng từ preset; chỉ bốn con số giả thiết ở §9.6 nằm trong script và được in riêng ở cuối
báo cáo để không ai nhầm chúng là số đo.

4. **Ba đến năm ảnh** của các phép kiểm ở bước 8, kèm ảnh hộp thoại của hai lệnh bảng.

Sau đó cập nhật lại **§9.6** của chính tài liệu này bằng số đo thật, và ghi một mục mới vào `AGENT.md`
theo đúng cách các mục §33–§41 đang ghi: số liệu, bằng chứng, và giới hạn claim.
