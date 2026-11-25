# 100 lệnh AutoCAD phổ biến - đối chiếu thao tác với iStCAD

Nguồn prompt: AutoCAD Command Reference (help.autodesk.com). Cột "iStCAD" ghi trạng thái ở v1.0.210:
**Khớp** = prompt, tùy chọn và thứ tự thao tác đã viết theo AutoCAD; **Một phần** = có lệnh nhưng thiếu tùy chọn ghi trong ngoặc;
**Có, chưa rà** = lệnh đã có nhưng chưa đối chiếu từng prompt; **Chưa có** = chưa có lệnh.
Mọi trạng thái "Khớp" là theo mã nguồn, chưa được kiểm bằng cách chạy thử.

## Vẽ
| # | Lệnh (alias) | Prompt AutoCAD | iStCAD |
|---|---|---|---|
| 1 | LINE (L) | Specify first point → Specify next point or [Close/Undo] | Khớp |
| 2 | PLINE (PL) | Specify start point → next point or [Arc/Close/Halfwidth/Length/Undo/Width] | Một phần (v1.0.221: đã làm Arc - gõ A để chuyển sang vẽ các đoạn cung tiếp tuyến với hướng polyline đang đi, gõ L để chuyển lại về đoạn thẳng, giống kiểu Arc mặc định của AutoCAD; lưu file dùng đúng Bulge trên LWPOLYLINE thật nên mở lại bằng AutoCAD vẫn là cung tròn, không phải polyline thẳng xấp xỉ. Thiếu Halfwidth/Length/Width và các tùy chọn con của Arc là Angle/CEnter/Direction/Radius/Second pt - chỉ làm kiểu tiếp tuyến mặc định) |
| 3 | CIRCLE (C) | center or [3P/2P/Ttr] → radius or [Diameter] | Khớp |
| 4 | ARC (A) | start point or [Center] → second point or [Center/End] → end point | Một phần (có 3 điểm và Center-Start-End; thiếu Start-Center-End/Angle/Length, Start-End-Radius/Direction) |
| 5 | RECTANG (REC) | first corner or [Chamfer/Elevation/Fillet/Thickness/Width] → other corner or [Area/Dimensions/Rotation] | Một phần (v1.0.218: đã rà, thêm Chamfer/Fillet (bo góc vuông vát/bo tròn 4 góc, nhớ giá trị cho lần vẽ sau giống AutoCAD); chưa làm Elevation/Thickness/Width (model polyline chưa có width theo đỉnh) và Area/Dimensions/Rotation sau khi chọn góc thứ 2 - ít dùng) |
| 6 | POLYGON (POL) | number of sides → center or [Edge] → [Inscribed/Circumscribed] → radius | Một phần (v1.0.219: ĐÃ SỬA LỖI - trước đây gõ POLYGON/POL lại mở nhầm 1 công cụ khác (vẽ vùng kín tự do bằng cách bấm từng điểm), không phải lệnh đa giác đều thật của AutoCAD; giờ gõ POLYGON/POL mới đúng ra lệnh đa giác đều số cạnh → tâm → bán kính (gõ số kèm chữ C ở cuối, ví dụ "40C", để lấy bán kính theo kiểu Circumscribed thay vì Inscribed mặc định). Chưa làm tùy chọn Edge (vẽ theo 1 cạnh thay vì tâm+bán kính) | 
| - | (đã bỏ) | công cụ "vùng kín tự do, bấm từng điểm" trước đây gán nhầm tên POLYGON | đã đổi: dùng PLINE rồi gõ C để đóng kín - cùng kết quả, không mất tính năng |
| 7 | ELLIPSE (EL) | axis endpoint or [Arc/Center] → other endpoint → distance to other axis or [Rotation] | Một phần (v1.0.220: cách nhập vẫn là tâm → bán trục 1 → bán trục 2 (gọn hơn, chủ ý giữ khác AutoCAD theo đúng góp ý "cách vẽ có thể khác, object tạo ra đúng native AutoCAD là ok"); ĐIỂM QUAN TRỌNG đã sửa: trước đây lưu file là 1 polyline 120 đoạn xấp xỉ hình ellipse, giờ lưu đúng thành 1 đối tượng ELLIPSE thật của AutoCAD (tâm, 2 bán trục, tỷ lệ) - mở bằng AutoCAD thật sẽ có grips/chỉnh sửa ellipse đúng chuẩn, không phải polyline giả. Đọc file có sẵn ELLIPSE (do AutoCAD tạo) cũng đã nhận đúng, không bị tách thành polyline. Chưa làm Arc (cung ellipse) và tùy chọn Rotation lúc vẽ.) |
| 8 | SPLINE (SPL) | first point or [Method/Knots/Object] → next point or [start Tangency/toLerance/Undo/Close] | Một phần (v1.0.222: ĐIỂM QUAN TRỌNG đã sửa - trước đây lưu file là 1 polyline nhiều đoạn thẳng xấp xỉ đường cong (Catmull-Rom), giờ dùng đúng công thức NURBS bậc 3 của chính ACadSharp (control points/knots/weights, cùng cách AutoCAD tính "fit points") nên lưu ra đúng 1 đối tượng SPLINE thật - mở bằng AutoCAD thật sẽ có đúng grips fit-point/control-point, không phải polyline giả. Đọc file có sẵn SPLINE cũng nhận đúng, không bị tách thành polyline. Cách nhập (bấm từng điểm, Enter để kết thúc) giữ đơn giản như cũ; chưa làm Method/Knots/Object/Tangency/toLerance/Close) |
| 9 | XLINE (XL) | point or [Hor/Ver/Ang/Bisect/Offset] → through point | Khớp (v1.0.211) |
| 10 | RAY | start point → through point | Chưa có |
| 11 | POINT (PO) | Specify a point | Chưa có (model chưa có đối tượng POINT) |
| 12 | DONUT (DO) | inside diameter → outside diameter → center of donut (lặp) | Có, chưa rà - ĐÃ PHÁT HIỆN vấn đề giống ELLIPSE/SPLINE trước khi sửa: hiện lưu ra 2 đối tượng CIRCLE rời, không phải 1 POLYLINE có Width (đúng kiểu AutoCAD lưu DONUT); model polyline hiện chưa có width theo đỉnh nên để sửa ở lần sau cùng lúc với RECTANG Width/PLINE Width |
| 13 | HATCH (H) | Pick internal point or [Select objects/Undo/seTtings] | Một phần (v1.0.213: chọn biên là chọn đối tượng kín có sẵn - đường tròn/polyline kín - không có "pick internal point" tự dò biên như AutoCAD; đơn giản và an toàn hơn cho người mới. Từ bản này mảng gạch là MỘT đối tượng duy nhất (không còn N đường LINE rời), có góc/khoảng cách chuẩn, dùng chung MOVE/COPY/ROTATE/SCALE/MIRROR/EXPLODE; lưu file sẽ ghi thành HATCH gốc để mở lại đúng) |
| 14 | BOUNDARY (BO) | Pick internal point | Chưa có |
| 15 | REVCLOUD | start point or [Arc length/Object/Rectangular/Polygonal/Freehand/Style/Modify] | Một phần (v1.0.223: click các điểm góc của vùng cần đánh dấu, Enter/C đóng lại - tự động chia thành các đoạn cung lồi ra ngoài đều nhau (kiểu mây mặc định), dùng đúng Bulge trên LWPOLYLINE thật nên mở AutoCAD vẫn là polyline có cung, không phải xấp xỉ. Chưa làm Arc length/Object/Rectangular/Polygonal/Freehand/Style/Modify - cỡ cung tự tính theo kích thước vùng vẽ, không hỏi riêng) |
| 16 | WIPEOUT | first point or [Frames/Polyline] | Một phần (v1.0.223: click các điểm góc của vùng cần che, Enter/C đóng lại - lưu đúng thành 1 đối tượng WIPEOUT thật của AutoCAD (InsertPoint/U-V vector/Size + ClipBoundaryVertices), AutoCAD mở ra sẽ che đúng phần bên dưới; RIÊNG trong màn hình iStCAD hiện tại MỚI CHỈ vẽ đường viền (outline), chưa thực sự che các đối tượng khác trên canvas của iStCAD - cần bạn kiểm tra kỹ khi mở bằng AutoCAD thật. Chưa làm Frames/Polyline options) |
| 17 | TEXT (DT) | start point or [Justify/Style] → height → rotation angle → nhập chữ | Một phần (đã rà: lưu đúng thành 1 đối tượng TEXT thật của AutoCAD [InsertPoint/Height/Rotation/WidthFactor/Horizontal+Vertical Alignment], đọc file có sẵn TEXT cũng nhận đúng - không có lỗi lưu sai kiểu. Cách nhập: điểm bắt đầu → cao chữ → góc xoay → gõ chữ, Enter là xong 1 lần gõ [khác AutoCAD gốc: Enter xong 1 dòng vẫn ở trong lệnh, gõ tiếp dòng khác - ở đây phải gõ lại lệnh TEXT cho dòng tiếp theo]. Chưa làm Justify [căn lề - app luôn lưu Left/Baseline mặc định, không hỏi chọn] và Style [luôn dùng kiểu chữ hiện hành, không hỏi đổi style khi đang vẽ]) |
| 18 | MTEXT (T, MT) | first corner → opposite corner or [Height/Justify/Line spacing/Rotation/Style/Width/Columns] | Một phần (đã rà: lưu đúng thành 1 đối tượng MTEXT thật của AutoCAD [RectangleWidth/LineSpacing/AttachmentPoint/AlignmentPoint theo đúng hướng xoay], đọc file có sẵn MTEXT cũng nhận đúng, tách đúng FormattedValue [có mã định dạng] và bản text thuần. Cách nhập cố ý đơn giản hơn: 1 điểm + cao chữ + góc xoay thay vì khung chữ nhật [first corner → opposite corner] của AutoCAD - chữ không tự xuống dòng theo khung [RectangleWidth lưu 0 = không giới hạn bề ngang], phải tự gõ \P để xuống dòng. Chưa làm Justify/Line spacing/Style/Width/Columns lúc nhập) |

## Chỉnh sửa
| # | Lệnh (alias) | Prompt AutoCAD | iStCAD |
|---|---|---|---|
| 19 | ERASE (E) | Select objects | Khớp |
| 20 | MOVE (M) | Select objects → base point or [Displacement] → second point or <use first point as displacement> | Khớp |
| 21 | COPY (CO, CP) | Select objects → base point or [Displacement/mOde] → second point or [Array] → lặp [Exit/Undo] | Một phần (v1.0.212: thêm U=Undo bản sao vừa đặt; còn thiếu mOde/Array - để sau, giữ lệnh đơn giản) |
| 22 | ROTATE (RO) | Select objects → base point → rotation angle or [Copy/Reference] | Khớp |
| 23 | SCALE (SC) | Select objects → base point → scale factor or [Copy/Reference] | Khớp |
| 24 | MIRROR (MI) | Select objects → first point of mirror line → second point → Erase source objects? [Yes/No] | Khớp |
| 25 | STRETCH (S) | Select objects (crossing) → base point or [Displacement] → second point | Một phần (đã rà: bắt buộc chọn bằng khung CROSSING [kéo phải→trái] đúng như AutoCAD, không nhận click/window thường - đúng tinh thần STRETCH. LINE/POLYLINE: đúng kiểu AutoCAD - chỉ đỉnh nào nằm trong khung mới di chuyển, đỉnh ngoài khung giữ nguyên [đường bị kéo giãn/biến dạng]. CIRCLE/ARC/TEXT/MTEXT: cả đối tượng di chuyển nguyên khối nếu điểm neo [tâm/điểm chèn] nằm trong khung, không có ngoại lệ - ARC thật của AutoCAD có thể biến dạng theo từng đầu mút nằm trong khung, ở đây đơn giản hoá thành di chuyển cả cung. Chưa làm Displacement [nhập độ dời bằng số thay vì bấm 2 điểm]) |
| 26 | OFFSET (O) | offset distance or [Through/Erase/Layer] → select object → point on side or [Exit/Multiple/Undo] | Khớp (v1.0.211) |
| 27 | TRIM (TR) | Select object to trim or shift-select to extend or [cuTting edges/Crossing/mOde/Project/eRase] | Một phần (chủ ý đơn giản hơn AutoCAD: MỌI đối tượng đang hiện đều tự động là cạnh cắt, không cần bước chọn cạnh cắt riêng - ít thao tác hơn cho người dùng; còn thiếu eRase) |
| 28 | EXTEND (EX) | Select object to extend or shift-select to trim or [Boundary edges/Crossing/mOde/Project] | Một phần (như TRIM - chủ ý bỏ bước chọn biên riêng để đơn giản hơn) |
| 29 | FILLET (F) | Select first object or [Undo/Polyline/Radius/Trim/Multiple] → second object | Một phần (v1.0.212: thêm T=Trim/No trim, U=Undo lần gần nhất; chọn liên tiếp nhiều cặp vốn đã được = Multiple; còn thiếu riêng CIRCLE) |
| 30 | CHAMFER (CHA) | Select first line or [Undo/Polyline/Distance/Angle/Trim/mEthod/Multiple] → second line | Một phần (v1.0.212: thêm T=Trim/No trim, U=Undo; còn thiếu Angle/mEthod - để sau, ít dùng, giữ lệnh đơn giản) |
| 31 | ARRAY (AR) | Select objects → [Rectangular/PAth/POlar] | Một phần (v1.0.223: đã thêm Path - gõ PA sau khi gõ ARRAY, rồi bấm chọn 1 đối tượng có sẵn (line/arc/circle/polyline/ellipse/spline) làm đường dẫn, nhập số lượng, chọn có tự xoay theo hướng đường dẫn hay không; các bản copy chia đều theo chiều dài cung trên đường dẫn - đơn giản hơn AutoCAD gốc (không hỏi Divide/Measure/Align riêng) theo đúng hướng "dễ nhập". ARRAY vẫn tạo bản copy độc lập, không phải "associative array" - xem giải thích riêng đã gửi) |
| 32 | EXPLODE (X) | Select objects | Khớp |
| 33 | JOIN (J) | Select source object or multiple objects to join | Khớp |
| 34 | BREAK (BR) | Select object → second break point or [First point] | Khớp |
| 35 | LENGTHEN (LEN) | Select an object to measure or [DElta/Percent/Total/DYnamic] | Khớp (v1.0.211) |
| 36 | PEDIT (PE) | Select polyline or [Multiple] → [Close/Join/Width/Edit vertex/Fit/Spline/Decurve/Ltype gen/Reverse/Undo] | Một phần (đã rà: Close/Open đổi đúng cờ Closed; Reverse đã kiểm kỹ lại công thức đảo Bulge [đảo thứ tự đỉnh, mỗi bulge đổi dấu VÀ dịch sang đỉnh liền trước - đúng vì bulge mô tả cung từ đỉnh này tới đỉnh kế tiếp] - đảo chiều mũi tên của polyline có cung tròn vẫn ra đúng hình, không bị méo. Join: CHỈ nối các đối tượng LINE liền nhau thành 1 polyline [đã ghi rõ trong gợi ý lệnh] - AutoCAD gốc còn nối được cả ARC và POLYLINE có sẵn vào chuỗi, ở đây chưa làm. Chưa làm Multiple/Width/Edit vertex/Fit/Spline/Decurve/Ltype gen/Undo) |
| 37 | ALIGN (AL) | Select objects → first source/destination point → second ... → Scale objects based on alignment points? | Một phần (đã rà: toán xoay+dịch đúng - tính góc xoay từ hướng cặp điểm nguồn/đích thứ 2 [không phụ thuộc khoảng cách], rồi xoay quanh điểm nguồn 1 + dịch điểm nguồn 1 trùng điểm đích 1; cặp điểm 2 chỉ dùng để lấy ĐÚNG HƯỚNG xoay, không dùng để co giãn - khớp đúng hành vi mặc định của AutoCAD khi trả lời "No" cho câu hỏi Scale. Chưa làm: hỏi Scale objects based on alignment points [luôn xử lý như chọn No]) |
| 38 | REVERSE | Select a line, polyline, spline or helix to reverse direction | Một phần (ĐÃ PHÁT HIỆN + SỬA LỖI khi rà: chọn 1 LINE rồi gõ REVERSE trước đây KHÔNG làm gì cả [chỉ xử lý POLYLINE, lặng lẽ báo "no polyline changed"], dù công cụ chọn đối tượng/gợi ý lệnh đều cho chọn LINE - giờ LINE cũng đổi đúng Start/End. POLYLINE dùng chung đúng công thức đảo Bulge đã kiểm kỹ ở PEDIT Reverse [đảo thứ tự đỉnh, mỗi bulge đổi dấu và dịch sang đỉnh liền trước]. Spline/Helix: app chưa có cách "đảo chiều" riêng cho SPLINE/HELIX nên REVERSE chưa áp dụng được cho 2 loại này) |
| 39 | OVERKILL (OV) | Select objects → hộp thoại | Có, chưa rà |
| 40 | OOPS | (không prompt) khôi phục đối tượng vừa ERASE | Khớp |
| 41 | MATCHPROP (MA) | Select source object → destination object(s) or [Settings] | Có, chưa rà |
| 42 | TEXTEDIT (ED) | Select an annotation object or [Undo/Mode] | Một phần (v1.0.215: gõ lệnh được, mở cùng hộp thoại double-click; chưa có Undo/Mode) |
| 43 | DIVIDE (DIV) | Select object to divide → number of segments or [Block] | Một phần (đã rà: toán chia đều theo đúng chiều dài thật của đối tượng - CIRCLE/ARC/POLYLINE [kể cả có cung/bulge] đều được "duỗi thẳng" [tessellate] đúng cách trước khi tính chiều dài và chia đều, dùng chung PolylineTessellator đã kiểm chứng kỹ ở các lệnh khác. Khác AutoCAD ở điểm quan trọng: đánh dấu mỗi điểm chia bằng 1 đoạn LINE ngắn vuông góc [không phải đối tượng POINT thật], đã tự báo rõ trong thông báo kết quả "no POINT objects" - không phải lỗi ẩn. Chưa làm tùy chọn Block [chèn 1 block có sẵn thay vì tick mark]) |
| 44 | MEASURE (ME) | Select object to measure → length of segment or [Block] | Một phần (như DIVIDE - đã rà, toán đúng, cùng giới hạn: tick mark bằng LINE thay vì POINT thật, chưa làm Block) |

## Kích thước và ghi chú
| # | Lệnh (alias) | Prompt AutoCAD | iStCAD |
|---|---|---|---|
| 45 | DIMLINEAR (DLI) | first extension line origin or <select object> → second → dimension line location or [Mtext/Text/Angle/Horizontal/Vertical/Rotated] | Có, chưa rà |
| 46 | DIMALIGNED (DAL) | như DIMLINEAR, [Mtext/Text/Angle] | Có, chưa rà |
| 47 | DIMANGULAR (DAN) | Select arc, circle, line, or <specify vertex> | Có, chưa rà |
| 48 | DIMRADIUS (DRA) | Select arc or circle → dimension line location | Có, chưa rà |
| 49 | DIMDIAMETER (DDI) | Select arc or circle → dimension line location | Có, chưa rà |
| 50 | DIMCONTINUE (DCO) | second extension line origin or [Select/Undo] | Một phần (v1.0.215: tự nối từ kích thước thẳng/song song vừa tạo, hoặc bấm chọn 1 cái có sẵn nếu chưa có; chưa có Undo riêng trong chuỗi) |
| 51 | DIMBASELINE (DBA) | second extension line origin or [Select/Undo] | Một phần (v1.0.215: tự tính khoảng cách giữa các đường kích thước theo kiểu hiện hành; chưa có Undo riêng trong chuỗi) |
| 52 | DIMORDINATE (DOR) | feature location → leader endpoint or [Xdatum/Ydatum/Mtext/Text/Angle] | Chưa có |
| 53 | QLEADER (LE) | first leader point or [Settings] → next point → text | Một phần (v1.0.216: đúng 1 đoạn gấp khúc cố định - điểm mũi tên → điểm gập, nhập chữ bằng hộp thoại; chưa có Settings, chưa chọn thêm điểm giữa) |
| 54 | MLEADER (MLD) | leader arrowhead location or [leader Landing first/Content first/Options] | Một phần (v1.0.224: cách nhập giống QLEADER - điểm mũi tên → điểm gập → nhập chữ; nhưng khi LƯU file giờ ghi ra đối tượng MULTILEADER thật của AutoCAD [không còn giả bằng LEADER cổ điển nữa], nên mở lại trong AutoCAD thật sẽ có đúng kiểu/grip/mũi tên của multileader. Vẫn đơn giản hoá: chỉ 1 leader line/1 root, không có block content, không có Options, không chọn thêm điểm giữa. Mở lại file .dwg/.dxf đã có MULTILEADER [do app này lưu trước đó, hoặc do AutoCAD thật tạo] trong iStCAD thì object đó hiện tạm như đối tượng chưa hỗ trợ [không bị hỏng/giảm cấp khi lưu lại, chỉ là chưa sửa được trong app] - đây là rủi ro còn mở, cần anh kiểm tra bằng AutoCAD thật) |
| 55 | DIMSTYLE (D) | hộp thoại Dimension Style Manager | Khớp (alias D đã đổi về DIMSTYLE) |
| 56 | DIMEDIT (DED) | [Home/New/Rotate/Oblique] | Chưa có |
| 57 | DIMCENTER (DCE) | Select arc or circle | Khớp (v1.0.211, dấu cộng nhỏ, chưa theo biến DIMCEN) |
| 58 | STYLE (ST) | hộp thoại Text Style | Có, chưa rà |

## Layer và thuộc tính
| # | Lệnh (alias) | Prompt AutoCAD | iStCAD |
|---|---|---|---|
| 59 | LAYER (LA) | Layer Properties Manager | Có, chưa rà |
| 60 | LAYOFF | Select an object on the layer to be turned off or [Settings/Undo] | Một phần (thiếu Settings/Undo; layer hiện hành không tắt) |
| 61 | LAYON | bật mọi layer | Khớp |
| 62 | LAYFRZ | Select an object on the layer to be frozen or [Settings/Undo] | Một phần (thiếu Settings/Undo) |
| 63 | LAYTHW | thaw mọi layer | Khớp |
| 64 | LAYLCK | Select an object on the layer to be locked | Khớp |
| 65 | LAYULK | Select an object on the layer to be unlocked | Khớp |
| 66 | LAYISO | Select objects on the layer(s) to be isolated or [Settings] | Một phần (kiểu Off; thiếu Settings Lock and fade) |
| 67 | LAYUNISO | khôi phục layer sau LAYISO | Khớp |
| 68 | LAYMCUR | Select object whose layer will become current | Khớp |
| 69 | LAYCUR | Select objects to be changed to the current layer | Khớp |
| 70 | PROPERTIES (PR, CH, MO) | bảng Properties | Có, chưa rà |
| 71 | LINETYPE (LT) | Linetype Manager | Có, chưa rà |
| 72 | LTSCALE (LTS) | Enter new linetype scale factor | Khớp (v1.0.213, mặc định 100 = chuẩn 1/100, áp dụng cho cả đối tượng cũ) |
| 73 | COLOR (COL) | Select Color | Khớp (v1.0.211, gõ tên/số màu, chưa mở hộp thoại Select Color) |

## Block
| # | Lệnh (alias) | Prompt AutoCAD | iStCAD |
|---|---|---|---|
| 74 | BLOCK (B) | hộp thoại Block Definition | Khớp (v1.0.217: nhập tên → chọn base point → chọn đối tượng (LINE/CIRCLE/ARC/POLYLINE/TEXT/MTEXT/ATTDEF) → Enter; thay các đối tượng đã chọn bằng 1 INSERT, lưu định nghĩa block vào bộ nhớ phiên làm việc, lưu file ra BlockRecord thật) |
| 75 | INSERT (I) | chọn block → insertion point or [Basepoint/Scale/Rotate] | Khớp (v1.0.217: thêm lựa chọn chèn 1 trong các block đã định nghĩa trong bản vẽ hiện tại (hộp thoại Name) bên cạnh chèn file ngoài cũ; nếu block có ATTDEF sẽ hỏi giá trị thuộc tính trước khi chèn; Scale/Rotate sau khi chèn dùng lệnh SCALE/ROTATE thường, chưa có tùy chọn ngay trong prompt) |
| 76 | WBLOCK (W) | hộp thoại Write Block | Chưa có (block chỉ lưu trong file hiện tại, chưa xuất ra file .dwg riêng) |
| 77 | ATTDEF (ATT) | hộp thoại Attribute Definition | Khớp (v1.0.217: hộp thoại Tag/Prompt/Default/Invisible/Justify/Height/Rotation → click đặt vị trí; hiển thị chữ Tag như AutoCAD cho đến khi được đưa vào BLOCK) |
| 78 | BEDIT (BE) | Block Editor | Chưa có |
| - | BATTMAN | hộp thoại Block Attribute Manager | Chưa có (sửa GIÁ TRỊ thuộc tính sau khi chèn dùng double-click → Enhanced Attribute Editor, đã có từ trước; BATTMAN để sửa ĐỊNH NGHĨA thuộc tính của block thì chưa có) |

## Truy vấn và chọn
| # | Lệnh (alias) | Prompt AutoCAD | iStCAD |
|---|---|---|---|
| 79 | DIST (DI) | first point → second point or [Multiple points] | Có, chưa rà |
| 80 | AREA (AA) | first corner point or [Object/Add area/Subtract area] | Có, chưa rà |
| 81 | LIST (LI, LS) | Select objects | Có, chưa rà |
| 82 | ID | Specify point | Có, chưa rà |
| 83 | QSELECT | hộp thoại Quick Select | Có, chưa rà |
| 84 | FILTER (FI) | hộp thoại Object Selection Filters | Có (form riêng của iStCAD) |
| 85 | SELECTSIMILAR | Select objects or [SEttings] | Có, chưa rà |
| 86 | SELECT | Select objects | Có (alias S đã trả về cho STRETCH) |

## Hiển thị, tệp, tiện ích
| # | Lệnh (alias) | Prompt AutoCAD | iStCAD |
|---|---|---|---|
| 87 | ZOOM (Z) | [All/Center/Dynamic/Extents/Previous/Scale/Window/Object] <real time> | Một phần (thiếu Dynamic) |
| 88 | PAN (P) | kéo chuột | Có, chưa rà |
| 89 | REGEN (RE) / REDRAW (R) | (không prompt) | Khớp |
| 90 | UNDO (U) / REDO | (không prompt) | Có, chưa rà |
| 91 | NEW / QNEW | tạo bản vẽ | Có, chưa rà |
| 92 | OPEN | mở tệp | Có, chưa rà |
| 93 | SAVE / QSAVE / SAVEAS | lưu tệp | Khớp tên lệnh |
| 94 | PLOT | hộp thoại Plot | Có (form riêng của iStCAD) |
| 95 | PURGE (PU) | hộp thoại Purge | Có, chưa rà |
| 96 | UNITS (UN) | hộp thoại Drawing Units | Chưa có |
| 97 | OSNAP (OS) / ORTHO / GRID | bật tắt chế độ vẽ | Một phần (OSNAP bật tắt, chưa mở hộp thoại) |
| 98 | COPYCLIP / CUTCLIP / PASTECLIP | clipboard | Có, chưa rà |
| 99 | SELECTALL (Ctrl+A) | chọn tất cả | Khớp |
| 100 | QUIT / EXIT | thoát | Có, chưa rà |
| 101 | TABLE (TB) | hộp thoại Insert Table (chọn style, số cột/hàng/kích thước, auto-fill...) | Một phần (v1.0.225: lệnh mới, cách nhập đơn giản hoá - gõ số cột → số hàng → bấm góc đầu → bấm góc đối diện [kéo cả 2 phương: rộng và cao cùng lúc, giống RECTANG], chia đều thành lưới. Lưu đúng thành 1 đối tượng TABLE thật của AutoCAD [TableEntity/AcDbTable]. Double-click vào 1 ô để nhập/sửa chữ ô đó. Chưa có hộp thoại Insert Table, chưa chọn style, chưa merge ô, chưa chỉnh riêng từng cột/hàng, ROTATE/MIRROR chưa áp dụng được cho TABLE [giữ nguyên vị trí cũ]. **Lưu ý quan trọng**: thư viện ACadSharp dùng trong app chỉ hỗ trợ ghi TABLE ra .dwg - ghi ra .dxf thư viện sẽ BỎ QUA đối tượng này [không lỗi, nhưng mất bảng] - chỉ nên dùng .dwg khi có TABLE) |
| 102 | VIEWPORT (MVIEW) | nhiều tuỳ chọn [Fit, 2/3/4 viewport, ON/OFF, Polygonal...], chỉ dùng được ở Layout/Paper space | Một phần (v1.0.226: lệnh mới, chỉ dùng được khi đang ở tab Layout [giống MVIEW thật - không cho tạo ở tab Model]. Cách nhập đơn giản hoá - chỉ cần kéo 1 hình chữ nhật trên giấy, iStCAD tự canh "view" bên trong bằng cách fit toàn bộ extents của Model space vào khung đó [giữ đúng tỷ lệ khung hình]. Lưu đúng thành 1 đối tượng VIEWPORT thật của AutoCAD [ACadSharp.Entities.Viewport] - đã kiểm tra: cả 2 writer DWG và DXF đều hỗ trợ ghi Viewport đầy đủ [không có rủi ro mất đối tượng khi đổi định dạng như TABLE/MULTILEADER]. Khi tạo viewport đầu tiên trên 1 Layout, iStCAD tự thêm 1 viewport "đại diện cho giấy" [Id=1, không hiển thị] trước, đúng quy ước của AutoCAD, để viewport thật của người dùng không bị nhận lầm thành viewport giấy khi mở lại file. Trên màn hình, khung viewport có hiển thị luôn nội dung Model space được cắt vào trong [dùng lại cơ chế vẽ viewport đã có sẵn trong app cho tab Plot]. Chưa có: tuỳ chọn Fit/2 viewport/3 viewport/4 viewport/Polygonal/ON-OFF riêng từng viewport, chưa có UI đóng băng layer riêng cho từng viewport [FrozenLayers đọc được khi import nhưng không sửa được], ROTATE/MIRROR chưa áp dụng được cho VIEWPORT [giữ nguyên vị trí cũ, giống TABLE], và đọc lại 1 file .dwg/.dxf có sẵn VIEWPORT từ AutoCAD cũng được nhận đúng thành đối tượng có thể chọn/di chuyển/scale) |

## Tổng hợp v1.0.210
- Khớp: 21 lệnh. Một phần: 16. Có, chưa rà: 43. Chưa có: 20.
- Thứ tự làm tiếp: (1) rà và sửa 43 lệnh "Có, chưa rà"; (2) bổ sung tùy chọn còn thiếu của 16 lệnh "Một phần"; (3) viết 20 lệnh "Chưa có".

## Tổng hợp v1.0.212 (giai đoạn 2b - ưu tiên đơn giản, dễ dùng)
- **COPY**: thêm U (Undo) khi đang đặt nhiều bản liên tiếp - gõ U để bỏ bản vừa đặt mà không phải hủy cả lệnh.
- **FILLET/CHAMFER**: thêm T (Trim/No trim - giữ nguyên hai đường, chỉ thêm cung/đường vát) và U (Undo lần bo/vát gần nhất). Việc bo/vát liên tiếp nhiều cặp vốn đã làm được sẵn (không cần gọi lại lệnh) nên coi như đã có "Multiple".
- **Chủ ý KHÔNG làm** (để giữ lệnh đơn giản, đúng theo yêu cầu "dễ dùng" của người dùng, không phải thiếu sót):
  - TRIM/EXTEND: không thêm bước "chọn cạnh cắt/biên trước" của AutoCAD gốc - hiện tại mọi đối tượng đang hiện trên bản vẽ tự động là cạnh cắt, người dùng bấm thẳng vào phần cần xén, ít bước hơn bản gốc.
  - CHAMFER: không làm Angle/mEthod (vát theo góc + 1 khoảng cách) - ít dùng, cộng thêm 2-3 bước hỏi nữa sẽ làm lệnh rối hơn.
  - COPY: không làm mOde (chọn Single/Multiple) vì mặc định "đặt nhiều bản liên tiếp" đã là hành vi hữu ích nhất cho đa số trường hợp; không làm Array (chia đều N bản copy giữa 2 điểm) vì ít dùng.
  - ARRAY: không làm Path (mảng theo đường cong) - phức tạp, ít dùng trong vẽ 2D thông thường.
- Tổng số không đổi so với v1.0.211: 26 Khớp / 13 Một phần / 43 Có-chưa-rà / 18 Chưa có (các lệnh trên vẫn ở "Một phần" vì chủ ý bỏ qua phần hiếm dùng, không phải vì chưa làm được).

## Tổng hợp v1.0.211 (giai đoạn 2a)
- Đã hoàn thiện đầy đủ tùy chọn còn thiếu: OFFSET (Erase/Layer/Multiple/Undo), XLINE (Hor/Ver/Ang/Bisect/Offset), LENGTHEN (DYnamic) → cả ba chuyển từ "Một phần" sang "Khớp".
- Đã viết mới: DIMCENTER (dấu cộng tại tâm arc/circle), COLOR (đặt màu hiện hành bằng tên/số/RGB gõ trực tiếp, chưa có hộp thoại Select Color) → cả hai chuyển từ "Chưa có" sang "Khớp".
- Kết quả: Khớp 26 / Một phần 13 / Có, chưa rà 43 / Chưa có 18 (tổng 100).
- Chưa làm trong lần này (còn lại của "Một phần", để giai đoạn sau): PLINE Halfwidth/Length/Width (model chưa có width theo đỉnh), ARC Start-Center-End/Angle/Length và Start-End-Radius/Direction, COPY mOde/Array/Undo, TRIM/EXTEND chọn cạnh cắt trước + eRase, FILLET/CHAMFER Trim/Multiple/Undo/Angle/mEthod, ARRAY PAth, LAYOFF/LAYFRZ Settings/Undo, LAYISO Settings (Lock&Fade), ZOOM Dynamic, OSNAP hộp thoại cài đặt.
- Thứ tự làm tiếp không đổi: (1) rà 43 "Có, chưa rà"; (2) hết phần "Một phần" còn lại; (3) 18 "Chưa có" (RAY, POINT, BOUNDARY, REVCLOUD, WIPEOUT, DIMCONTINUE, DIMBASELINE, DIMORDINATE, QLEADER, MLEADER, DIMEDIT, LTSCALE, WBLOCK, ATTDEF, BEDIT, UNITS, TEXTEDIT — RAY/POINT cần thêm kiểu đối tượng mới trong model, LTSCALE cần thêm trường vào CadDrawing, nên để sau).
