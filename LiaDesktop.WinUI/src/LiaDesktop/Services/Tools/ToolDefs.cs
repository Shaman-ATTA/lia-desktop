namespace LiaDesktop.Services.Tools;

public static class ToolDefs
{
    public static readonly object[] Declarations =
    [
        Decl("list_dir", "Список файлов и папок", ("path", "Путь к папке", false)),
        Decl("read_file", "Прочитать текстовый файл", ("path", "Путь к файлу", true)),
        Decl("write_file", "Записать текстовый файл", ("path", "Путь", true), ("content", "Содержимое", true)),
        Decl("run_powershell", "Выполнить команду PowerShell", ("command", "Команда", true)),
        Decl("look_at_screen",
            "Посмотреть на экран: захват primary-монитора (DPI-aware) и возврат изображения модели. " +
            "Вызывай ПЕРВОЙ, когда пользователь говорит «что на экране», «посмотри», «нажми на …».",
            ("path", "Куда сохранить (опц.)", false)),
        Decl("screenshot",
            "Скриншот primary-монитора (как look_at_screen): изображение возвращается модели + файл на диск.",
            ("path", "Куда сохранить", false)),
        Decl("browser_open", "Открыть URL в браузере Playwright", ("url", "URL", true)),
        Decl("browser_close", "Закрыть браузер Playwright"),
        Decl("type_text", "Набрать текст клавиатурой", ("text", "Текст", true)),
        Decl("mouse_click",
            "Клик мышью. X/Y — пиксели ПОСЛЕДНЕГО скриншота (look_at_screen/screenshot), не DIP. " +
            "Рантайм сам пересчитает в physical screen pixels (DPI 100–300%).",
            ("x", "X в координатах изображения скриншота", true),
            ("y", "Y в координатах изображения скриншота", true)),
        Decl("list_windows", "Список окон верхнего уровня (UI Automation)"),
        Decl("focus_window", "Сфокусировать окно по заголовку или имени процесса",
            ("title", "Заголовок или process name", true)),
        Decl("wait_for_window", "Ждать появления окна (вместо blind sleep)",
            ("title", "Заголовок или process name", true),
            ("timeout_ms", "Таймаут мс (по умолч. 15000)", false)),
        Decl("find_element", "Найти UI-элемент по имени (UIA)",
            ("name", "Name / AutomationId", true),
            ("window", "Окно (опц.)", false)),
        Decl("click_element", "Кликнуть UI-элемент по имени (UIA) — предпочтительнее mouse_click по координатам",
            ("name", "Name / AutomationId кнопки", true),
            ("window", "Окно (опц.)", false)),
        Decl("remember_fact", "Сохранить долгий факт о пользователе",
            ("fact", "Факт", true)),
        Decl("recall", "Вспомнить факты (опц. фильтр)",
            ("query", "Подстрока поиска", false)),
        Decl("forget_fact", "Забыть факт по id или подстроке",
            ("query", "Id или текст", true)),
    ];

    static object Decl(string name, string description, params (string Name, string Desc, bool Required)[] parms)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in parms)
        {
            props[p.Name] = new { type = "string", description = p.Desc };
            if (p.Required) required.Add(p.Name);
        }
        return new
        {
            name,
            description,
            parameters = new
            {
                type = "object",
                properties = props,
                required,
            },
        };
    }
}
