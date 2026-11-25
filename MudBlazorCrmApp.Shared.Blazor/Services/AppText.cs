namespace MudBlazorCrmApp.Shared.Blazor.Services;

/// <summary>
/// Provides Persian/English UI captions and AGENT_* keys for both Blazor UI and the AI Agent.
/// </summary>
public class AppText
{
    public string Language { get; private set; } = "fa";
    public bool IsRtl => Language == "fa";

    private static readonly Dictionary<string, (string Fa, string En)> Entries = new(StringComparer.Ordinal)
    {
        // App & Navigation
        ["APP_TITLE"] = ("مدیریت مشتریان", "CRM"),
        ["NAV_DASHBOARD"] = ("داشبورد", "Dashboard"),
        ["NAV_PIPELINE"] = ("بورد پایپ‌لاین", "Pipeline Board"),
        ["NAV_CALENDAR"] = ("تقویم", "Calendar"),
        ["NAV_SALES_GROUP"] = ("فروش", "Sales"),
        ["NAV_LEADS"] = ("سرنخ‌ها", "Leads"),
        ["NAV_OPPORTUNITIES"] = ("فرصت‌ها", "Opportunities"),
        ["NAV_SALES"] = ("فروش‌ها", "Sales"),
        ["NAV_ACTIVITIES"] = ("فعالیت‌ها", "Activities"),
        ["NAV_CUSTOMERS_GROUP"] = ("مشتریان", "Customers"),
        ["NAV_COMPANIES"] = ("شرکت‌ها / مشتریان", "Companies"),
        ["NAV_CONTACTS"] = ("مخاطبین", "Contacts"),
        ["NAV_ADDRESSES"] = ("آدرس‌ها", "Addresses"),
        ["NAV_PRODUCTS_GROUP"] = ("محصولات و خدمات", "Products & Services"),
        ["NAV_PRODUCTS"] = ("محصولات", "Products"),
        ["NAV_PRODUCT_CATEGORIES"] = ("دسته‌بندی محصولات", "Product Categories"),
        ["NAV_SERVICES"] = ("خدمات", "Services"),
        ["NAV_SERVICE_CATEGORIES"] = ("دسته‌بندی خدمات", "Service Categories"),
        ["NAV_VENDORS"] = ("تامین‌کنندگان", "Vendors"),
        ["NAV_SUPPORT_GROUP"] = ("پشتیبانی", "Support"),
        ["NAV_SUPPORT_CASES"] = ("تیکت‌های پشتیبانی", "Support Cases"),
        ["NAV_COMMUNICATIONS"] = ("ارتباطات", "Communications"),
        ["NAV_TASKS_GROUP"] = ("وظایف", "Tasks"),
        ["NAV_TASK_LIST"] = ("لیست وظایف", "Task List"),
        ["NAV_KANBAN_BOARD"] = ("بورد کانبان", "Kanban Board"),
        ["NAV_REWARDS"] = ("پاداش‌ها", "Rewards"),
        ["NAV_TAGS"] = ("برچسب‌ها", "Tags"),
        ["NAV_TOOLS_GROUP"] = ("ابزارها", "Tools"),
        ["NAV_REPORTS"] = ("گزارش‌ها", "Reports"),
        ["NAV_IMPORT_DATA"] = ("ورود داده (CSV)", "Import Data"),
        ["NAV_EMAIL_TEMPLATES"] = ("قالب‌های ایمیل", "Email Templates"),
        ["NAV_ADMIN_GROUP"] = ("مدیریت سیستم", "Administration"),
        ["NAV_USERS"] = ("کاربران", "Users"),
        ["NAV_AUDIT_LOG"] = ("گزارش تغییرات", "Audit Log"),
        ["NAV_CHANGE_PASSWORD"] = ("تغییر رمز عبور", "Change Password"),
        ["NAV_LOGOUT"] = ("خروج", "Logout"),
        ["NAV_REGISTER"] = ("ثبت‌نام", "Register"),
        ["NAV_LOGIN"] = ("ورود", "Login"),
        ["HEADER_SEARCH_PLACEHOLDER"] = ("جستجو...", "Search..."),
        ["HEADER_NOTIFICATIONS"] = ("اعلان‌ها", "Notifications"),
        ["HEADER_MARK_ALL_READ"] = ("خواندن همه", "Mark all read"),
        ["HEADER_NO_NOTIFICATIONS"] = ("اعلانی وجود ندارد", "No notifications"),
        ["HEADER_THEMES"] = ("پوسته", "Themes"),
        ["HEADER_ABOUT"] = ("درباره", "About"),
        ["DASHBOARD_TITLE"] = ("داشبورد CRM", "CRM Dashboard"),
        ["DASHBOARD_WELCOME"] = ("به داشبورد CRM خوش آمدید", "Welcome to the CRM Dashboard"),

        // Common Actions (used for risk classification by key)
        ["ACTION_ADD"] = ("افزودن", "Add"),
        ["ACTION_EDIT"] = ("ویرایش", "Edit"),
        ["ACTION_SAVE"] = ("ذخیره", "Save"),
        ["ACTION_DELETE"] = ("حذف", "Delete"),
        ["ACTION_CANCEL"] = ("انصراف", "Cancel"),
        ["ACTION_EXPORT"] = ("خروجی CSV", "Export"),
        ["ACTION_SEARCH"] = ("جستجو", "Search"),

        // Agent Panel (AGENT_*)
        ["AGENT_TITLE"] = ("دستیار هوشمند", "AI Assistant"),
        ["AGENT_BUTTON_TOOLTIP"] = ("باز کردن دستیار هوشمند", "Open AI Assistant"),
        ["AGENT_INPUT_PLACEHOLDER"] = ("دستور یا سوال خود را بنویسید...", "Ask a question or give a task..."),
        ["AGENT_SEND"] = ("ارسال", "Send"),
        ["AGENT_STOP"] = ("توقف", "Stop"),
        ["AGENT_NEW_CHAT"] = ("گفتگوی جدید", "New Chat"),
        ["AGENT_MODE_READONLY"] = ("فقط خواندنی", "Read-Only"),
        ["AGENT_MODE_MANUAL"] = ("دستی (تایید همه)", "Manual"),
        ["AGENT_MODE_SEMIAUTO"] = ("نیمه‌خودکار", "Semi-Auto"),
        ["AGENT_MODE_AUTO"] = ("خودکار", "Auto"),
        ["AGENT_PERM_ALLOW_ONCE"] = ("اجازه این بار", "Allow Once"),
        ["AGENT_PERM_ALLOW_ALWAYS"] = ("همیشه اجازه بده", "Always Allow"),
        ["AGENT_PERM_DENY"] = ("رد کردن", "Don't Allow"),
        ["AGENT_PERM_PROMPT"] = ("تایید عملیات: {0} ({1})", "Approve action: {0} ({1})"),
        ["AGENT_THINKING"] = ("در حال بررسی...", "Thinking..."),
        ["AGENT_STOPPED"] = ("توسط کاربر متوقف شد.", "Stopped by user."),
        ["AGENT_EMPTY_STATE"] = ("می‌توانید درباره صفحه فعلی، جابه‌جایی بین بخش‌ها یا وضعیت CRM سوال بپرسید.", "Ask about the current screen, navigate between pages, or inspect CRM info."),
        ["AGENT_STATS_FORMAT"] = ("فراخوانی: {0} | ورودی: {1} | خروجی: {2}", "Calls: {0} | In: {1} | Out: {2}"),
        ["AGENT_STATS_COST_FORMAT"] = (" | هزینه: ${0:0.0000}", " | Cost: ${0:0.0000}"),
        ["AGENT_HISTORY"] = ("تاریخچه گفتگوها", "Chat History"),
        ["AGENT_NO_HISTORY"] = ("گفتگوی ذخیره‌شده‌ای وجود ندارد", "No saved chats"),
        ["AGENT_ERROR_PREFIX"] = ("خطا: ", "Error: ")
    };

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (Entries.TryGetValue(key, out var pair))
        {
            return Language == "fa" ? pair.Fa : pair.En;
        }
        return key;
    }

    public void SetLanguage(string lang)
    {
        Language = lang == "en" ? "en" : "fa";
    }
}
