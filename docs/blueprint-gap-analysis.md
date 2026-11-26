# تحلیل جامع شکاف‌ها و باگ‌ها (6-Lens Audit: `MudBlazorCrmApp` در برابر `ai-agent-blueprint/reference`)

> **تاریخ ممیزی:** 2026-11-26  
> **دامنهٔ مقایسه:** `C:\Projects\AI Agent\MudBlazorCrmApp\` در برابر `C:\Projects\AI Agent\ai-agent-blueprint\reference\` و مستندات راهنمای Blueprint  
> **روش کار:** ممیزی فقط-خواندنی با ۶ لنز مستقل (**Security**، **Frontend & Driver**، **Backend & Providers**، **Database & Cost**، **Prompts & Schemas**، **QA & Contract**). برای هر مورد، هم **مکان دقیق در پروژه** و هم **فایل و سمبل مرجع** ثبت شده است.

---

## جمع‌بندی مدیریتی (یافته‌های بحرانی کشف‌شده در این ممیزی)

علاوه بر قابلیت‌های جاافتاده نسبت به Blueprint، این ممیزی چند **باگ فعال و ریسک امنیتی جدی** در کدهای فعلی را آشکار کرد:
1. **باگ‌های OData و UI Driver در اجرای فعلی:**
   - منابع `leads` و `todo_tasks` در `AgentDataService` روی فیلدهای غیرواقعی (`Name` و `Title`) فیلتر OData می‌زنند و هنگام `search` خطای **HTTP 400** می‌دهند.
   - کلید فارسی `"به‌روزرسانی"` (`UI_UPDATE`) در `classifyRisk` فایل `agentDriver.js` نیست؛ بنابراین دکمه‌های آپدیت با ریسک `write` به جای `save` دسته‌بندی شده و اعتبارسنجی فیلدهای اجباری را دور می‌زنند.
   - کامپوننت‌های `MudDatePicker` (به‌علت `readonly` پیش‌فرض) و `MudAutocomplete` (به‌علت `blur` قبل از اتمام `DebounceInterval`) در `agentDriver.js` عملاً خراب‌اند.
   - `LookupTermHandler` با `appText[term]` کار می‌کند (کلید دیکشنری می‌خواهد نه عبارت فارسی/انگلیسی) و ترجمهٔ دوزبانه انجام نمی‌دهد.
   - `list_attachments` / `read_attachment` / `ask_attachment` از مدل پارامتر `chatKey` می‌خواهند و `upload_file_to_screen` محتوای خام base64 از مدل می‌خواهد (در حالی که مرجع از `context.ChatKey` و `attachmentId` استفاده می‌کند).
2. **شکاف‌های بحرانی امنیتی و محافظت:**
   - ابزارهای دانش مشترک (`remember`, `correct_fact`, `delete_fact`) بدون چک دسترسی/نقش کاربر و با هویت هاردکدشدهٔ `"agent"` روی کل دیتابیس مشترک تغییر می‌دهند؛ `AgentKnowledgeGuard` هم الگوی فارسی ندارد و `Topic` را اصلاً فیلتر نمی‌کند.
   - `AllowAlways` برای عملیات `save`/`write`/`confirm` مجاز است، `Agent.Mode` بدون `isTrusted` است و پورتال `MudSelect` آن بیرون از `[data-agent-panel]` رندر می‌شود، و `#agent-file-input` نیز بیرون از `[data-agent-panel]` قرار دارد.
   - `RedactAttachmentBlocks` تعریف شده ولی در `AgentChatController` صدا زده نمی‌شود و کلاینت هم نتایج فایل را پاک (blank) نمی‌کند.
   - `/auditlog` و `/import` در `BlockedRoutes` نیستند و `[[action|...|Route]]` هم بدون چک `BlockedRoutes` ناوبری می‌کند.
3. **دیتابیس و مایگریشن:**
   - فایل `20261126120000_AddAgentKnowledge.Designer.cs` متد `BuildTargetModel` خالی دارد و `AgentKnowledge` در `ApplicationDbContextModelSnapshot.cs` نیست (که با `PendingModelChangesWarning` پنهان شده است).
   - مصرف توکن و هزینهٔ `ask_knowledge` و `ask_attachment` دور ریخته می‌شود (`var (json, _) = ...`) و ۲ باگ منطقی در شرط `PurgeIfDueAsync` وجود دارد.

---

## لنز ۱: امنیت، ایمنی و گاردهای مجوز (Security & Safety Lens)

### یافته‌های Critical
| # | شرح دقیق باگ / شکاف | مکان در `MudBlazorCrmApp` | نمونه و سمبل در `ai-agent-blueprint/reference` |
|---|---|---|---|
| **S-C1** | نبود گارد مجوز روی ابزارهای دانش مشترک (`remember`, `correct_fact`, `delete_fact`)، هاردکد بودن `"agent"` و `"fa"`، نبود گارد بررسی صفحه/فایل پیوست، و `Remove` مستقیم به جای غیرفعال‌سازی جهت بازبینی انسانی | `Services/Agent/AgentKnowledgeTools.cs:12,35,39-58`<br>`Services/Agent/AgentKnowledgeService.cs:72-112,163-233`<br>`Services/Agent/AgentService.cs:87` | `reference/backend/Knowledge/AgentKnowledgeWriteGuard.cs:8-17` (`DeniedAsync`, `AgentKnowledgeEdit`)<br>`RememberServerTool.cs:7-15`, `DeleteFactServerTool.cs:7-15`<br>`AgentKnowledgeEditor.cs:17-73` (`HasAttachments`, `HasCheckedInApplication`, `MaxFactChangesPerTask`, `ShownFactIds`)<br>`AgentKnowledgeService.cs:116-131,177-193` |
| **S-C2** | نبود الگوهای فارسی در `AgentKnowledgeGuard`، **فیلتر نشدن فیلد `Topic`**، نبود نرمال‌سازی کاراکترهای کنترلی (`zero-width`)، حساسیت به حروف در `SnakeCaseRegex`، و جا ماندن عبارات انگلیسی مثل `disregard` / `without confirmation` / `auto-mode` / `pwd` | `Services/Agent/AgentKnowledgeGuard.cs:15-49` (`ValidateWrite`, `PassesReadFilter`)<br>`Services/Agent/AgentKnowledgeService.cs:55,135` | `reference/backend/Knowledge/AgentKnowledgeFilter.cs:14-83` (`_sensitive`, `_instruction`, `_panelControl`, `Refusal`, `IsUsable`, `Normalize`) |
| **S-C3** | مجاز بودن `AllowAlways` برای ریسک‌های `save`، `write`، `confirm` و ناشناخته (در مرجع فقط برای عملیات سبک مثل `export`/`clearFilters` مجاز است) و نبود دکمهٔ پاک‌کردن مجوزهای همیشگی | `MudBlazorCrmApp.Shared.Blazor/Components/AgentPanel.razor:234-239`<br>`Services/Agent/AgentPermissionGate.cs:60-61,75-76` | `reference/frontend/guards/toolGuards.ts:160-163,191,200-209`<br>`lightKinds.ts:11-14` (`lightKindOf`)<br>`alwaysAllowStore.ts:10-27` (`clearAlwaysAllowed`)<br>`clickActionTool.ts:133-138`, `ModePicker.tsx:122-133` |
| **S-C4** | نبود بررسی `isTrusted` روی تغییر `Agent.Mode`، قفل نشدن Selector حین اجرا، و استفاده از `MudSelect` که پاپ‌اور آن (`.mud-popover-open`) بیرون از `[data-agent-panel="true"]` رندر می‌شود | `Components/AgentPanel.razor:111-115,298-302`<br>`Services/AgentClientService.cs:30,351`<br>`wwwroot/js/agentDriver.js:229-240,321-355` | `reference/frontend/ui/ModePicker.tsx:16-36,72-73,85-98` (`dropUntrusted`, inline menu)<br>`reference/frontend/driver/panelGuard.ts:8-16` (`isInsideAgentPanel`, `isUntrustedEvent`)<br>`reference/frontend/ui/AgentPanel.tsx:183-187,304` (`modeRef`) |
| **S-C5** | فراخوانی نشدن `RedactAttachmentBlocks` در `AgentChatController`، ناتوانی این متد در پاک‌سازی رشته‌های JSON-escaped، رپ نشدن CSV/XLSX در `ReadPagedRows` با `<attachment>`، و نبود blanking در کلاینت | `Controllers/AgentChatController.cs:58-163`<br>`Services/Agent/AgentAttachmentService.cs:135-157,185,226`<br>`Services/AgentClientService.cs:259,301,348,380` | `reference/backend/Persistence/AgentChatService.cs:48-49,65-67`<br>`reference/backend/Attachments/AgentAttachmentWrapper.cs:10-107` (`Wrap`, `Redact`, `RedactToken`)<br>`ReadAttachmentServerTool.cs:30,47`<br>`reference/frontend/core/attachments/attachmentReads.ts:19-58`, `attachmentMarks.ts:14-38`, `agentLoop.ts:367,461-466,537` |

### یافته‌های High / Medium / Low
| سطح | # | شرح باگ / شکاف | مکان در `MudBlazorCrmApp` | نمونه در `ai-agent-blueprint/reference` |
|---|---|---|---|---|
| **High** | **S-H1** | نبود `replacedDeleteGuard` (امکان ویرایش/غیرفعال‌سازی جایگزین پس از بلاک شدن حذف رکورد در حال استفاده) | `UiToolHandlers.cs:106-200`, `AgentSafetyState.cs:8-80` | `reference/frontend/guards/replacedDeleteGuard.ts:12-28`, `toolGuards.ts:53-56`, `agentLoop.ts:335-336` |
| **High** | **S-H2** | نبود مارکر `Delete task: yes` در پیام اول، نبود `IsReadBeforeDelete` در سرور و نبود `deleteReadBlock` (`inUseSummary` از `read_data`) قبل از کلیک Delete | `AgentClientTranscriptHelper.cs:11-27`, `AgentServerGuard.cs:132-165`, `UiToolHandlers.cs:179-184` | `reference/frontend/core/taskMessage.ts:58,66`, `reference/backend/Guards/AgentServerToolRepeat.cs:56-86`, `toolGuards.ts:53-71`, `clickActionTool.ts:126-129` |
| **High** | **S-H3** | معیوب بودن چرخهٔ `deleteRefused`: در کلاینت با رد کاربر/پنل ست می‌شود به جای رد اپلیکیشن؛ در سرور فقط `read_data` را می‌بندد نه سایر ابزارهای سرور (و اجازهٔ ۱ خواندن مسدودکننده را هم نمی‌دهد) | `UiToolHandlers.cs:183,189,191-199`, `AgentUiToolDispatcher.cs:47-84`, `AgentServerGuard.cs:145-148,206-214` | `reference/frontend/guards/toolGuards.ts:38-51,73-77`, `agentLoop.ts:329-334`, `AgentServerToolRepeat.cs:88-109` |
| **High** | **S-H4** | نبود `autoModeAdviceGuard` و دور زدن `FinishHandler` توسط پاسخ‌های متنی بدون `tool_calls` | `UiToolHandlers.cs:323-334`, `AgentClientService.cs:310-327` | `reference/frontend/guards/autoModeAdviceGuard.ts:10-21`, `agentLoop.ts:307-312,405-414` |
| **High** | **S-H5** | عدم بررسی `delegatedBy`, `wrongRecordBlock`, `fileValueBlock` (تزریق فرمول `= + - @`), `needsFileSaveQuestion`, `armDeleteStep` (تایید ۶۰ ثانیه‌ای دیالوگ حذف) و پیش‌فرض ریسک `'write'` به جای `'ask'` برای دکمه‌های ناشناخته | `UiToolHandlers.cs:106-200`, `AgentSafetyState.cs:10-25`, `AgentPermissionGate.cs:83-135`, `agentDriver.js:38-53` | `toolGuards.ts:80-93,110-143,174-190`, `deleteTarget.ts:47-107`, `setFieldTool.ts:92-107`, `attachmentGuards.ts:17-53`, `actions.ts:25-65` |
| **High** | **S-H6** | ناقص بودن `BlockedRoutes` (غیاب `/auditlog` و `/import` و زیرمسیرها) و عبور ویجت `[[action|...|Route]]` از روی `BlockedRoutes`؛ همچنین نبود `[Authorize(Roles="Administrator")]` روی `AuditLogController` | `AgentUiToolDispatcher.cs:23-26,107-127`, `agentDriver.js:411`, `AgentWidgetParser.cs:36-52`, `AuditLogController.cs:13` | `reference/frontend/driver/navigation.ts:112-189`, `06-safety-and-permissions.md` §3, `safety-set.md` S02 |
| **High** | **S-H7** | پذیرش `MediaType` دلخواه و حجم کنترل‌نشدهٔ تصویر در `LookAsync` بدون بررسی `image/jpeg`/`image/png`، سقف 4MB دیکدشده و پرچم `Vision` | `Services/Agent/AgentService.cs:225-285` | `reference/backend/Look/AgentLookService.cs:13-48` |
| **High** | **S-H8** | ریسک DoS حافظه در پارس XLSX: نبود `AgentTableBudget` (۵۰ شیت × ۵۰۰۰ سطر × ۲۰۰ ستون مجاز)، محاسبهٔ حجم فایل فشرده در `TryAdmit` به جای حجم حافظهٔ جدول، و نبود تایم‌اوت پارس | `Services/Agent/AgentAttachmentParser.cs:53-135`, `AgentAttachmentService.cs:65-74,188-206` | `reference/backend/Attachments/AgentTableBudget.cs:5-37`, `XlsxAttachmentParser.cs:25-66`, `AgentOpenXmlGuard.cs:8-60`, `AgentAttachmentService.cs:28-30,156-192,222-229` |
| **High** | **S-H9** | نبود کنترل Pixel-Bomb (سقف ۲۵M پیکسل) و بازکدگذاری (برای حذف EXIF/polyglot) در تصاویر پیوست + باگ نادیده‌گرفتن تصویر در `AskAttachmentAsync` | `AgentAttachmentParser.cs:26-31,137-143`, `AgentAttachmentService.cs:111-133` | `reference/backend/Attachments/ImageAttachmentParser.cs:9-41`, `AgentAttachmentSettingInfo.cs:14,22,46`, `AskAttachmentServerTool.cs:31-35,55-71` |
| **High** | **S-H10** | خواندن نامحدود بدنهٔ Loopback با `ReadAsStringAsync` بدون `ResponseHeadersRead`، سقف بایت (32-64MB) و تایم‌اوت ۲۰ ثانیه | `Services/Agent/AgentDataService.cs:66-78,114-144` | `reference/backend/Data/AgentLoopbackClient.cs:25-30,82-92,114-139`, `AgentLoopbackAddress.cs:13-23` |
| **High** | **S-H11** | باگ `RecordAndCheckError` (شمارش خطا با هر آرگومان روی یک ابزار و بازپخش نشدن از transcript) و حذف نشدن `ask_help` از اسکیمای ابزارها در سقف ۳ بار | `AgentServerGuard.cs:132-185`, `AgentService.cs:117` | `reference/backend/Guards/AgentServerToolRepeat.cs:22-37`, `AgentHelpAskHistory.cs`, `AgentService.cs:212-215` |
| **Med** | **S-M1** | `MaxTranscriptMessages = 200` (مرجع `400`) و نبود سقف کل کاراکترهای `MAX_TOTAL_MESSAGE_CHARS` (`4_000_000`) و اعتبارسنجی JSON آرگومان‌ها در ورودی | `AgentService.cs:17`, `AgentServerGuard.cs:11-51` | `reference/backend/AgentService.cs:31-32,174-199` |
| **Med** | **S-M2** | نبود اعتبارسنجی طول/کاراکترهای کنترلی (`AgentDataParameters`) برای `search`/`status` و نداشتن محدودیت نود در `EnableQueryFeatures()` | `AgentDataService.cs:58-59,91-107`, `Program.cs:67` | `reference/backend/Data/AgentDataParameters.cs:13-41` |
| **Med** | **S-M3** | `[AllowAnonymous]` روی `GET /api/agent/status` (افشای تنظیمات بودجه و وضعیت فعال‌بودن ایجنت) و تغییر singleton `AgentOptions` با `Normalize()` | `Controllers/AgentController.cs:15-31` | `reference/backend/Controllers/AgentController.cs`, `reference/frontend/core/agentEnabled.ts:11` |
| **Med** | **S-M4** | نبود `AgentAttachmentPolicy` (آپلود فایل برای LLMهای خارجی به‌طور پیش‌فرض باز است)، نبود rate-limit آپلود، نبود `ChatKeyRegex` در `AgentAttachmentController` و ثبت بدنهٔ خطای LLM در لاگ | `AgentAttachmentController.cs:17-25`, `AgentAttachmentService.cs:43-87`, `OpenAiCompatibleAgentProvider.cs:72` | `AgentAttachmentBodyLimit.cs:7-18`, `AgentAttachmentPolicy.cs:11-55`, `AgentAttachmentService.cs:37-49,55-66,194-198` |
| **Med** | **S-M5** | قرار گرفتن `<InputFile id="agent-file-input">` **بیرون از** `[data-agent-panel="true"]` و نبود گارد مجوز/`fileInputState` روی `UploadFileToScreenHandler` | `AgentPanel.razor:11,19`, `UiToolHandlers.cs:267-284`, `agentDriver.js:560` | `reference/frontend/tools/uploadFileTool.ts`, `agentLoop.ts:228`, `panelGuard.ts:8-10` |
| **Med** | **S-M6** | اعتماد سرور به `ModelCallsSoFar` و `CostSoFarUsd` ارسالی کلاینت و خروج ناگهانی کلاینت در سقف بودجه بدون فرصت `[final call]` | `AgentServerGuard.cs:109-127`, `AgentClientService.cs:285-289` | `reference/backend/Guards/AgentFinalCall.cs`, `reference/frontend/core/agentLoop.ts:358-361,494-506` |
| **Low** | **S-L1** | افشای مقادیر فیلدهای `password` در `captureSnapshot`، `setField`، `Ledger` و `TranscriptJson` | `agentDriver.js:105,306`, `UiToolHandlers.cs:116-120`, `AgentClientService.cs:345,380` | `setFieldTool.ts:102,123,126`, `agentLoop.ts:442-444` |
| **Low** | **S-L2** | برگرداندن متن خام `ex.Message` به LLM/کلاینت در خطاهای Loopback و UI Tool | `AgentDataService.cs:153`, `AgentUiToolDispatcher.cs:82` | `reference/backend/Data/AgentLoopbackClient.cs:110` |

---

## لنز ۲: فرانت‌اند و درایور DOM (Frontend & DOM Driver Lens)

### ۱. ابزارها و قابلیت‌های جاافتاده در `agentDriver.js` و `UiToolHandlers.cs`
| قابلیت / ابزار | وضعیت در `MudBlazorCrmApp` | مرجع در `ai-agent-blueprint/reference` |
|---|---|---|
| `scan_menus` | غایب در `UiToolHandlers.cs`, `agentDriver.js`, `AgentToolSchemas.cs` | `frontend/tools/screenActionTools.ts:48`, `driver/menus.ts:96-145` |
| `scan_context_menu` و توالی کامل `right_click` | `scan_context_menu` غایب؛ `rightClick` (`agentDriver.js:577-593`) فقط `contextmenu` می‌فرستد بدون pointer sequence و زیرمنو | `frontend/tools/screenActionTools.ts:46-47`, `driver/menus.ts:52-91,148-154` |
| `scan_tabs` (خواندن فیلدهای خالی/خطادار همه تب‌ها) | غایب | `frontend/tools/scanTabsTool.ts:13-29`, `driver/scanTabs.ts:19-156`, `dom.ts:10-29` |
| `scroll_grid` (اسکرول افقی ستون‌ها) | غایب | `frontend/tools/scrollGridTool.ts:14-17`, `driver/scrollGrid.ts:27-58` |
| `select_tree_node` و `search_settings` + اسنپ‌شات درخت | غایب | `selectTreeNodeTool.ts:11-16`, `searchSettingsTool.ts:11-16`, `driver/tree.ts:35-106`, `treeSearch.ts` |
| `multiSelect` در کامبو (`open_combo(multi)`, `pick_option(texts/removeTexts/all/apply)`, `close_combo(apply)`) | ۱۱ صفحه از `MultiSelection="true"` استفاده می‌کنند (مثل `AddContact.razor:56`, `ModifyRoles.razor:30`) ولی `agentDriver.js:96-111` و هندلرها پشتیبانی ندارند | `driver/readFields.ts:46,113-118`, `multiCombo.ts:50-348`, `pickOptionTool.ts:52-84`, `screenActionTools.ts:49-59` |
| `scan_screen(rows)` و متادیتای کامل اسنپ‌شات | `maxRows = 15` ثابت (`agentDriver.js:145`)؛ نبود `aria-rowcount`، `windowWidth`، `filters`، `gridFooters`؛ ارسال اضافی `navRoutes` در هر اسنپ‌شات (`L215`) | `scanScreenTool.ts:11-14`, `driver/scanScreen.ts:16-56`, `readGrids.ts:12-13,47-143`, `urlQuery.ts:20-36` |
| سلول‌های قابل ویرایش گرید، `leaveRow`, `lineIsKept` | گرید فقط آرایهٔ متنی ساده برمی‌گرداند (`agentDriver.js:144-153`) بدون ویرایش خطی | `readGrids.ts:75-91`, `fieldEditors.ts:31-42,116-149`, `leaveRow.ts:33-43`, `lineKept.ts` |
| `fileInputs` در اسنپ‌شات و `upload_file_to_screen` با `attachmentId` | `fileInputs` اسکن نمی‌شود؛ ابزار به اشتباه base64 `content` از مدل می‌خواهد (`UiToolHandlers.cs:267-284`) | `driver/readFileInputs.ts:19-65`, `snapshot.ts:113`, `tools/uploadFileTool.ts:43-106` |
| `capturePicture` و فرمت غنی `readScreenText` | `capturePicture` غایب؛ `captureVisibleText()` (`agentDriver.js:242-248`) روی `cloneNode` جداشده `innerText` می‌خواند (باگ رندر/نشت متن مخفی) | `driver/capture.ts:14-42`, `driver/screenText.ts:19-127`, `tools/lookAtScreenTool.ts:14-52` |
| `filterRestore` (بازگرداندن فیلترها در پایان تسک) | `clearFilters` (`agentDriver.js:498-522`) فقط در خطای لحظه‌ای برمی‌گرداند؛ `AgentClientService` در `finally` بازنشانی نمی‌کند و `ClearFiltersHandler` مجوز نمی‌گیرد | `core/filterRestore.ts:13-49`, `driver/filters.ts:65-106`, `tools/clearFiltersTool.ts` |
| `go_to_page` با جستجوی هدر، `n`، `titleMismatch` و `settle` با `MutationObserver` | `AgentUiToolDispatcher` فقط لینک‌های `NavMenu` را می‌خواند نه سرچ هدر (`NavMenu.razor:17-35`)؛ `waitForSettle` فقط `setTimeout` دارد | `driver/navigation.ts:97-189`, `screenActionTools.ts:29-43,61-78`, `driver/settle.ts:20-67`, `readActions.ts:155-334` |

### ۲. باگ‌های کشف‌شده در کدهای فرانت‌اند موجود و نواقص UX پنل
- **باگ `MudDatePicker` (`Pages/Reports.razor:24,27`, `ListAuditLog.razor:39,42`):** بدون `Editable="true"` اینپوت `readonly` ساخته می‌شود؛ `agentDriver.js:110` آن را `disabled: true` ثبت کرده و `setField` (`L275`) رد می‌کند (`reference/frontend/driver/fieldEditors.ts:151-172`).
- **باگ `MudAutocomplete` (`agentDriver.js:97-100,300-305`):** بلافاصله روی `requestAnimationFrame` بعدی `blur()` صدا می‌زند قبل از اتمام `DebounceInterval` (250ms) و بدون انتخاب آیتم لیست (`reference/frontend/driver/fieldEditors.ts:44-78`).
- **باگ `LookupTermHandler` (`UiToolHandlers.cs:302-311`):** از `appText[term]` استفاده می‌کند که کلید فنی می‌خواهد (نه کپشن نمایش‌داده‌شده) (`reference/frontend/core/agentDictionary.ts:56-86`).
- **باگ `SetFieldHandler` (`UiToolHandlers.cs:117`):** حتی اگر `setField` نتیجه `{ok:false}` بدهد یا فیلد از نوع جستجوی گرید باشد، `Safety.MarkDirty()` صدا زده می‌شود (`reference/frontend/core/saveTracker.ts:52-64`).
- **باگ `OnTaskKeyDown` (`AgentPanel.razor:450-456`):** زدن Enter هنگام `IsUploadingAttachment` برخلاف دکمهٔ Send مسدود نیست (`TaskComposer.tsx:80,103-108`).
- **نواقص UX پنل و اتوماسیون:**
  - غیاب `CopyButton` (`CopyButton.tsx`), `options[]` در `ask_user` (`AgentPanel.tsx:513-551`), `helpNoteView` + `groupLog` + `StepGroup` (`helpNoteView.ts`, `groupLog.ts`, `LogItem.tsx`), Drag & Drop و Paste فایل (`useFileDrop.ts`, `TaskComposer.tsx:97-102`), و `data-no-focus-lock="true"` + `stopImmediatePropagation` (`AgentPanel.tsx:205-216`).
  - نبود دکمهٔ موبایل در هدر و فراخوانی تکراری `GET /api/agent/status` در `NavMenu.razor:267` و `AgentPanel.razor:398` (`mountPoints.snippet.txt:7-16`, `agentEnabled.ts:11`).
  - وجود تنها **۲۴ کلید `AGENT_*`** در `AppText.cs:71-94` در برابر **۱۴۱+ کلید** در مرجع + وجود رشته‌های فارسی هاردکدشده در `AgentPanel.razor` و `AgentClientService.cs`.
  - غیاب **۱۱ `data-testid` استاندارد** از `automation-spec.md`: `agent-run` (در پروژه `agent-send` است)، `agent-working`، `agent-step` (`data-blocked`)، `agent-question`، `agent-question-text`، `agent-question-option`، `agent-question-other-input`، `agent-question-other-send`، `agent-result`، `agent-said`، `agent-ledger` (`agent-ledger-wrote`, `agent-ledger-opened`)، `agent-error`، `agent-attach-input`، `agent-attach-chip`.

---

## لنز ۳: بکند، پرووایدرها، Help و Data (Backend Core Lens)

| حوزه | شکاف یا ناهماهنگی در `MudBlazorCrmApp` | مکان در پروژه | مرجع در `ai-agent-blueprint/reference` |
|---|---|---|---|
| **حلقه‌ی Step** | نبود اجرای موازی `firstCall` با AutoHelp و استفاده مجدد در صورت skip، و نبود AutoHelp بعد از خطای ابزار (`MAX_AUTO_HELPS=2`) | `Services/Agent/AgentService.cs:43-58,63-93` | `reference/backend/AgentService.cs:72-108`<br>`Help/AgentHelpService.cs:64-116` |
| **System Prompt & Cache** | چسباندن `autoHelp` و `notes` به انتهای `system` فقط در `Count==1` (شکست Prompt Cache و گم‌شدن در step 2+) به جای تزریق پیام `user` در `prior` | `Services/Agent/AgentService.cs:43-47,74-92` | `reference/backend/AgentService.cs:51,62-66,92-97,201-205` |
| **Providers** | نبود `AnthropicAgentLlmProvider` و `AgentLlmProviderFactory`؛ نبود retry روی 429/5xx/529، تایم‌اوت 60s هر تلاش، `cache_control`، fallback خطای 400 `thinking`، `FastModel`/`VisionModel`/`MaxTokens`، خواندن `x-litellm-response-cost` و حذف `reasoning_content` | `OpenAiCompatibleAgentProvider.cs:23-118`, `AgentOptions.cs:3-50`, `Program.cs:195-201` | `Providers/AnthropicAgentLlmProvider.cs:23-335`, `AgentLlmProviderFactory.cs:9-17`, `OpenAiCompatibleAgentLlmProvider.cs:28-97`, `Support/AgentModelClient.cs:13-41`, `Settings/AgentSettingInfo.cs:12-40` |
| **Rate Limiter** | نبود باکت‌های مجزای `step:`/`look:`/`attachment:`، نبود rate limit روی آپلود پیوست، و نبود سقف روزانه (`MaxStepsPerDay=5000`) | `Services/Agent/AgentAbstractions.cs:30-55`, `AgentAttachmentService.cs:43-87` | `Support/AgentRateLimiter.cs:10-60`, `Look/AgentLookService.cs:25`, `Attachments/AgentAttachmentService.cs:63` |
| **Help System** | نبود `HelpSessionStore`، `AgentHelpHealth` (60s circuit breaker)، `AgentHowTo.Parse`، `SearchHelpServerTool`، امتیازدهی BM25/IDF (`AgentHelpRanker`) و `AgentHelpText` (`FocusOn`, `CleanHelp`) | `Services/Agent/AgentHelpService.cs:5-99`, `AgentHelpLlmAdvisor.cs:10-84`, `AgentServerTools.cs:11-41` | `HelpExtras/HelpSessionStore.cs:7-28`, `AgentHelpHealth.cs:7-49`, `SearchHelpServerTool.cs:10-39`, `AgentHelpText.cs:6-58`, `Help/AgentHelpRanker.cs:15-109`, `Guards/AgentHowTo.cs:26-57` |
| **`read_data` & CRM** | تنها ۹ منبع هاردکدشده (غیاب `Product`, `Service`, `Vendor`, `Invoice`/Billing, `Communication`, `Reward`, `ReportController`, `DashboardController`)؛ نبود `IAgentDataSource`/`IAgentDataCatalog`، `AgentDataProjection` (`Pick`, `Totals`, `statusCounts`, `scope`, `note`) و ارسال فقط هدر `Authorization` در loopback (بدون کوکی) | `Services/Agent/AgentDataService.cs:12-226` | `Data/AgentDataService.cs`, `IAgentDataSource.cs`, `AgentDataProjection.cs:10-196`, `AgentLoopbackClient.cs:23-151`, `Support/AgentDataCatalog.cs`, `Data/Sources/OrderCountAgentDataSource.cs:34-114` |
| **Attachments & Look** | نبود `GET api/agent/attachment/chat/{chatKey}`، نبود `AgentAttachmentResolver` (`[att:<id>]` در transcript)، نبود `RequestDecoderMiddleware`/حذف بدنه آپلود از لاگ، و نبود `AgentEnabledFilter`/`AgentAttachmentsEnabledFilter`/`[NotDelayable]`/`AddAgent()` | `Controllers/AgentAttachmentController.cs:11-42`, `AgentController.cs:15-61`, `Program.cs:169-202` | `Controllers/AgentEnabledFilter.cs`, `Middlewares/AgentEnabledAttribute.cs`, `Attachments/AgentAttachmentsEnabledFilter.cs`, `AgentAttachmentResolver.cs`, `AgentAttachmentBodyLimit.cs`, `Di/AddAgent.cs:14-51` |
| **Charts (اختیاری)** | ویژگی اختیاری پیشنهاد نمودار از ستون‌های گرید (`POST api/Agent/Charts`) پیاده‌سازی نشده است | — | `reference/backend/Charts/AgentChartService.cs:16-348`, `Controllers/AgentController.cs:35` |

---

## لنز ۴: دیتابیس، ماندگاری و محاسبهٔ هزینه (Database & Cost Lens)

1. **ناهماهنگی‌های اسکیما و EF Core Snapshot:**
   - `20261126120000_AddAgentKnowledge.Designer.cs:15-17` متد `BuildTargetModel` خالی دارد و `AgentKnowledge` اصلاً در `ApplicationDbContextModelSnapshot.cs` ثبت نشده (با `Program.cs:87` `PendingModelChangesWarning` مخفی شده است).
   - ایندکس‌های `IX_AgentChat_UserId_ChatKey` و `IX_AgentTask_UserId_ChatKey_TaskSeq` با SQL خام در `20261125232124_AddAgentTables.cs:25,43` ساخته شده‌اند و در `OnModelCreating` / Snapshot نیستند؛ ایندکس `IX_AgentChat_UpdatedDate` (`AgentChatHistory-Standalone.sql:21-22`) هم کلاً غایب است.
   - در `AgentTask` (`AgentPersistenceModels.cs:37-46`): `Goal` سقف `[MaxLength(2000)]` دارد (مرجع `nvarchar(MAX)`), توکن‌ها `int` هستند به جای `bigint` و `Cost`/`CachedInputTokens` غیرقابل-Null هستند (در نتیجه با `ShowCost=false` یا قیمت نامعلوم امکان ذخیرهٔ `NULL` نیست).
2. **نبود جدول و کش `AgentModelPrice` و محاسبهٔ چندنرخی:**
   - پروژه جدول، استور و کش `AgentModelPrice` (`reference/sql/AgentModelPrice-*.sql`, `dbo.AgentModelPriceGet.PRC`, `reference/backend/Prices/AgentModelPriceCache.cs:11-58`) را ندارد و فقط از ۲ عدد ثابت در `AgentOptions.cs:18-19` و تخفیف ثابت `0.25m` (`AgentAbstractions.cs:59`) استفاده می‌کند (بدون `CacheRead`, `CacheWrite5m/1h`, `LongPromptThreshold` و برگرداندن `null` برای مدل ناشناس).
3. **گم شدن Usage ابزارهای جانبی (نبود `AgentUsageCounter`):**
   - `AskKnowledgeTool` (`AgentKnowledgeTools.cs:22`) و `AskAttachmentTool` (`AgentAttachmentTools.cs:56`) مقدار `Usage` برگشتی را دور می‌ریزند (`var (json, _) = ...`)؛ در مرجع `AgentUsageCounter` (`reference/backend/Providers/AgentUsageCounter.cs:13-73`) به صورت `Scoped` کل تماس‌های اصلی و جانبی را جمع می‌زند.
4. **باگ‌ها و نواقص نگهداری چت و دانش:**
   - نبود `HistoryDays` و پاکسازی خودکار (`Retention Purge`) در `AgentChatController.PutAsync` (`AgentChatHistory-Standalone.sql:86-95`, `AgentChatService.cs:51`).
   - `AgentChatController.DeleteAsync` (`L166-181`) پیوست‌های چت را از `AgentAttachmentService` پاک نمی‌کند (`AgentChatService.cs:83`).
   - `AgentChatStoreClient.cs:82` همیشه `logJson = "[]"` می‌فرستد و خطای `AgentChatSaveResponse.Error` را نادیده می‌گیرد؛ `LoadChatAsync` (`AgentClientService.cs:101`) شمارندهٔ توکن/هزینه را صفر می‌کند؛ همگام‌سازی Debounced (`1500ms`)، Circuit Breaker بعد از ۳ خطا (`chatRemote.ts:14-57`)، کش `localStorage`/`sessionStorage` (`chatStore.ts:38-80`) و برش `6000`/`1500` کاراکتر نتایج در `buildTaskRecord` (`taskRecord.ts:10-44`) وجود ندارند.
   - در `AgentKnowledgeService.PurgeIfDueAsync` (`L235-251`): نادیده گرفتن `ModifiedDate`، شرط اشتباه `&&` به جای `OR` در `LastUsedDate`/`UseCount`، هاردکد بودن `90`/`3`، نداشتن `try/catch` ایزوله (خطای Purge کل KB را ۵ دقیقه قطع می‌کند)، بازنشانی نشدن `NotHelpedCount = 0` در `RememberAsync`/`CorrectFactAsync` و ریست نشدن `_unavailableUntilTicks` پس از موفقیت (`AgentKnowledge-Standalone-Round2.sql:66-68,221,287-290`, `AgentKnowledgePurger.cs`, `AgentKnowledgeHealth.cs`).

---

## لنز ۵: پرامپت‌ها، اسکیمای ابزارها و واژگان دوزبانه (Prompt & Schema Lens)

1. **نواقص `AgentPrompts.MainSystemPrompt` (`AgentPrompts.cs:8-32`) نسبت به `system-prompt.full.md`:**
   - برچسب منسوخ `Available capabilities (Phase 4):` و جا انداختن ۱۰ ابزار فعال (دانش، پیوست، اکشن بیزنسی) از متن پرامپت.
   - یکپارچه بودن در یک `const` بدون تقسیم‌بندی ماژولار و بدون الحاق `DataTools.PromptText`، `KNOWLEDGE_PROMPT` و `ATTACHMENT_PROMPT` در `AgentService` (`side-prompts.md:72-112`, `AgentService.cs:201-205`).
   - نبود قوانین کلیدی مرجع: ممنوعیت سوئیچ دیتابیس/تِنانت، پیش‌چک حذف با `read_data`، نحوهٔ رفتار پس از `deleteRefused`، بستن دیالوگ باقی‌مانده از تسک قبل، شمارش از `footerCount` نه سطرهای مرئی/بریده‌شده، مدیریت فیلترهای فعال و باکس Search در برابر حالت Edit، بازخوانی پس از ذخیره، قوانین بخش `Reports` (`Pages/Reports.razor`), و نحوهٔ پاسخ یک‌مرحله‌ای به سوالات `how-to`.
2. **ناهمخوانی‌های بحرانی اسکیمای ابزارها (`AgentToolSchemas.cs` vs `tool-schemas.json` & هندلرها):**
   - **باگ OData در `AgentDataService.cs:26,31`:** `leads` روی `SearchField = "Name"` (در `Lead.cs` فیلد `Name`/`Email`/`Value` اصلاً وجود ندارد، `Company`/`Title`/`PotentialValue` دارد) و `todo_tasks` روی `SearchField = "Title"` (در `TodoTask.cs` فیلد `Name`/`Description` دارد) سرچ OData می‌سازند و با HTTP 400 می‌شکنند؛ `sales` هم `SearchField = null` دارد.
   - `upload_file_to_screen` (`AgentToolSchemas.cs:400-416`): پارامترهای `{id, fileName, content, contentType}` می‌خواهد به جای `{id, attachmentId}` (`tool-schemas.json:747-766`).
   - `list_attachments`, `read_attachment`, `ask_attachment` (`AgentToolSchemas.cs:351-398`): از مدل `chatKey` می‌خواهند به جای خواندن از `context.ChatKey` (`tool-schemas.json:687-745`).
   - `correct_fact`, `delete_fact`, `fact_did_not_help` (`AgentToolSchemas.cs:305-349`): نام پارامتر `id` است به جای `fact_id` (`tool-schemas.json:574-684`).
   - `look_at_screen` (`L242`): `enum: ["text", "vision"]` به جای `["text", "picture"]` (`tool-schemas.json:42-65`).
   - `ask_user` (`L263-274`): فاقد `options[]` (`tool-schemas.json:429-451`).
   - `GetHowToTools()` (`L8-12`): فقط ۳ ابزار دارد؛ `read_data`, `lookup_term`, `ask_knowledge`, ابزارهای پیوست و `search_help` را کم دارد (`Guards/AgentHowTo.cs:24`).
   - `RunBusinessActionHandler` (`BusinessToolRegistry.cs:49-71`): از `CheckFinishOrNavAllowed()` رد نمی‌شود و `OpenOverdueTasksAction` هم روی `/KanbanTodoTask` فیلتر overdue ندارد.
3. **سایدپرامپت‌های ضعیف/هاردکد و شکاف‌های دوزبانه (`crm-guide.md` و صفحات):**
   - پرامپت‌های `AskKnowledgeAsync` (`AgentKnowledgeService.cs:138`) و `AskAttachmentAsync` (`AgentAttachmentService.cs:119`) داخل سرویس‌ها هاردکد شده‌اند؛ `HelpKeywordsSystemPrompt`, `HelpDistillSystemPrompt`, `LookSystemPrompt` قوانین خروجی مرجع (`HOWTO|DO|NONE`, `NOT COVERED`, سقف خط، `LOOK_TEXT_SYSTEM`) را ندارند (`side-prompts.md:5-132`).
   - ۵ صفحه در `HelpDocs/crm-guide.md` غایب‌اند (`/address`, `/communication`, `/reward`, `/tag`, `/emailtemplate`)، آدرس‌ها PascalCase نوشته شده‌اند ولی `LowercaseUrls = true` است، و نام دکمه‌ها با فارسی صفحه ناهماهنگ است.
   - ۱۷ صفحهٔ `List*.razor` (و `Reports.razor`، `PipelineBoard.razor`، `CalendarView.razor`، `ImportData.razor`) هنوز رشته‌های انگلیسی هاردکد دارند (مثل `>Add<` به جای `@L["ACTION_ADD"]`) و `agentDriver.js:609` (`rec.lang = 'fa-IR'`) هم برای حالت انگلیسی ثابت روی فارسی مانده است.

---

## لنز ۶: تست، قرارداد و اتوماسیون E2E (QA & Contract Lens)

1. **تست‌های ارزیابی 40Q و Safety Set (`AgentEvaluation40QTests.cs:14-189`):**
   - `FortyQuestions()` از متن‌های جنریک و یک `EvalLlmProvider` / `EvalUiHost` استاب‌شده در یک تک step استفاده می‌کند، نه سوالات واقعی `docs/40-question-eval-set.md` در مرورگر و دیتابیس واقعی؛ دستهٔ **۹ سوال `TRAP`** و **`Q29` (حذف با هدف مبهم)** اصلاً وجود ندارد (`reference/tests/question-set-template.md:24-33,331-409`).
   - تست `PermanentSafetySet_S01ToS10_Passes100Percent` فقط ۱۰ چک واحد است؛ پروب‌های واقعی `S01–S16`، گاردهای `G1–G5c`، پذیرش حذف `D1–D7` و `DV-*` (`reference/tests/safety-set.md:11-153`) و پیگیری `scoring-sheet.md` / `regression-history.md` غایب‌اند.
2. **نبود چارچوب Playwright `AGENT_ASK` (`reference/tests/automation-spec.md`):**
   - ردیف `E2E` در `docs/teamwork-architecture-plan.md:52` هنوز `PLANNED` است؛ هیچ تست Playwright، `AskParam`, `PermissionPolicy`, `StatsParser`, `TaskRunner`, `PanelDriver`، **Assertion سه‌لایه** (`contains/matches` + `DB_EXPECT` + `LLM_ASSERT`)، `Negative Control`، گیت `Agent:Available` و `seedMarked`/`cleanupMarked` وجود ندارد.
   - حتی متن نمایش stats در `AppText.cs:90` (`Calls: {0} | In: {1} | Out: {2}`) با `StatsParser` مرجع (`/tokens used/i` + `cached`) و دکمهٔ `"Allow Once"` (`AppText.cs:81`) با `"Allow This Once"` ناهماهنگ است.
3. **نبود Contract Tests (`reference/contract/contract.md` §8):**
   - هیچ تست قراردادی برای چک یکپارچهٔ تمام ۳۰ ابزار `AgentToolSchemas` با هندلرها، تست راندتریپ مارکرها (`Task:` vs `Goal:`، `Delete task: yes`، `Help ... sources:`، `[att:...]`, `[final call]`, `deleteRefused`, `inUseSummary`)، و تست مرز محدودیت‌ها (`400` پیام / `4M` کاراکتر، `3M` ذخیره چت، `Look` limits، `hop == 4` + ابزار ترکیبی) وجود ندارد.
4. **فاصلهٔ تست‌های واحد (۳ فایل در پروژه در برابر ۵۴ فایل `*Tests.cs` در مرجع + ۰ تست JS/TS):**
   - حوزه‌های بدون تست واحد در `MudBlazorCrmApp.Tests`:
     - `AgentDataService` و کلاینت Loopback (`reference/backend/Tests/AgentLoopbackClientTests.cs:20-239`, `AgentDataProjectionTests.cs`)
     - `OpenAiCompatibleAgentProvider` خطاهای 429/503/401/Timeout/usage (`AgentModelClientTests.cs`, `AnthropicAgentLlmProviderThinkingTests.cs`)
     - تست‌های کامل `AgentAttachmentParser` و `AgentAttachmentService` برای XLSX، Zip-Bomb، DTD، WebP/JPG، ردیف‌های صفحه‌بندی، سهمیه و `AgentAttachmentController` (۱۸ فایل تست در `reference/backend/Attachments/*Tests.cs`)
     - تست‌های کامل `AgentChatController` CRUD، جداسازی کاربر، سقف 200/3M و Redaction (`reference/backend/Persistence/`)
     - تست‌های سرویس `AgentKnowledgeService` / `AgentKnowledgeTools` (۷ فایل تست در `reference/backend/Knowledge/*Tests.cs`)
     - تست‌های فرانت‌اند برای `AgentClientService`، `AgentUiToolDispatcher`، ۱۳ هندلر تست‌نشده در `UiToolHandlers.cs` و `agentDriver.js`.

---

## اولویت‌بندی پیشنهادی برای رفع شکاف‌ها
1. **فوری (اصلاح باگ‌های فعال + حفره‌های بحرانی امنیتی):**
   - اصلاح `SearchField` در `AgentDataService` (`leads`, `todo_tasks`, `sales`)، افزودن `"به‌روزرسانی"` به `classifyRisk` در `agentDriver.js`، اصلاح `MudDatePicker`/`MudAutocomplete`/`LookupTermHandler` و `captureVisibleText`.
   - رفع باگ‌های Knowledge (`AgentKnowledgeGuard` فارسی + فیلتر `Topic` + `UserId` واقعی + حذف دکمه/مجوز غیرمجاز)، بستن `AllowAlways` برای `save`، انتقال `#agent-file-input` و انتخابگر `Mode` به داخل `[data-agent-panel]` با چک `IsRunning`/`isTrusted`، فعال‌سازی `RedactAttachmentBlocks` و حذف پیوست‌های چت، افزودن `/auditlog` و `/import` به `BlockedRoutes` + فیلتر ویجت `[[action]]`.
2. **قرارداد فرانت/بک و پایداری حلقه:**
   - یکسان‌سازی اسکیمای ابزارها (`upload_file_to_screen` با `attachmentId`، `chatKey` از context، `fact_id`، `ask_user` با `options`، تکمیل `HowToTools`)، پیاده‌سازی `AppendLedger` + Loop Detection کلاینت + `[final call]` پیش‌دستانه، `replacedDeleteGuard`، `autoModeAdviceGuard`، `filterRestore`، اصلاح `ModelSnapshot` مایگریشن `AddAgentKnowledge`، و جمع‌آوری Usage ابزارهای جانبی با `AgentUsageCounter`.
3. **تکمیل قابلیت‌های مرجع و تضمین کیفیت E2E:**
   - اجرا: اجرای موازی گام اول + `prior` برای Help/Knowledge، `HelpSessionStore`/`AgentHelpHealth`/BM25، حالت تصویر واقعی `look_at_screen` + `AskAttachmentAsync` تصویری، `AgentTableBudget` + پیکسل‌چک تصاویر، `HistoryDays` Purge، و پیاده‌سازی Contract Tests + سوئیت واقعی Playwright (`automation-spec.md` + `safety-set.md`).

---

## وضعیت رفع شکاف‌ها (بروزرسانی 2026-11-27 — Wave 1، Wave 2 و PR #1)

| حوزه | موارد برطرف‌شده در `Wave 1` (`2a6795d`)، `Wave 2` (`a2ee249`) و `PR #1` | موارد باقی‌مانده برای گام‌های بعدی |
|---|---|---|
| **امنیت و مجوزها (`S-C1`–`S-C5`, `S-H1`–`S-H11`)** | گاردهای دوزبانه و دسترسی دانش (`AgentKnowledgeGuard`, `AgentKnowledgeWriteGuard`)؛ محدودسازی `AllowAlways` به `light`/`export`؛ انتقال `<InputFile>` و انتخابگر `Mode` به داخل پنل با قفل `IsRunning`؛ فعال‌سازی `RedactAttachmentBlocks` و پاک‌سازی پیوست‌ها در `AgentChatController`؛ `replacedDeleteGuard`، `DeleteTaskRequested`، `autoModeAdviceGuard`، `wrongRecordBlock`، `fileValueBlock`؛ مسدودسازی `/auditlog` و `/import` و `[Authorize(Roles="Administrator")]` در `AuditLogController`؛ اعتبارسنجی `LookAsync`؛ `AgentTableBudget` و محدودیت ابعاد تصویر؛ محدودیت بایت/تایم‌اوت Loopback؛ گارد خطای تکراری و `IsHowToQuestion` با پشتیبانی از `Goal:` و `[att:...]` | پیاده‌سازی `AnthropicAgentLlmProvider` و `AgentLlmProviderFactory` برای انتخاب پویا از `AgentOptions.Provider`؛ تکمیل `BuildTargetModel` در `20261126120000_AddAgentKnowledge.Designer.cs` |
| **فرانت‌اند و درایور DOM** | اصلاح `SearchField` برای `leads` و `todo_tasks`؛ پشتیبانی `MudDatePicker`/`MudAutocomplete`/`LookupTermHandler`/`captureVisibleText`؛ افزودن ابزارهای `scan_menus`، `scan_tabs`، `scan_context_menu`، `scroll_grid`، `select_tree_node`، `search_settings`، `clear_filters`، `sort_grid`، `right_click`، `upload_file_to_screen` و `BusinessToolRegistry`؛ پیاده‌سازی `AgentLoopGuards` و `AppendLedger` | پشتیبانی کامل `multiSelect` (افزودن/حذف تکی)، `MudDatePicker` `Editable="true"` روی صفحات قدیمی، و بومی‌سازی کامل دکمه‌های باقی‌مانده در صفحات `List*.razor` |
| **پرامپت، Help و هزینه** | بازنویسی و تکمیل `AgentPrompts.MainSystemPrompt`؛ جمع‌آوری مصرف جانبی با `AgentUsageCounter`؛ چانک‌سازی H2/H3 راهنما (`SectionParser`, `HelpSection`, `AgentHelpService.GetPage`) با محافظ code-fence و بازنویسی دقیق بخش ۱۵ `crm-guide.md`؛ فعال‌سازی `FastModel`، `VisionModel`، `MaxStepsPerDay` و `HistoryDays`؛ پشتیبانی `BuildInitialContextAsync` در گفتگوهای چندمرحله‌ای | جدول و کش قیمت‌گذاری چندنرخی `AgentModelPrice`؛ رتبه‌بندی BM25 در `AgentHelpService` و `HelpSessionStore`؛ سوئیت کامل Playwright `AGENT_ASK` |

*وضعیت تست واحد پس از PR #1: **۶۷/۶۷ پاس (`dotnet test`)**.*
