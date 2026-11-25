namespace LiaDesktop.Services.Tools;

public static class ToolDefs
{
    public static readonly object[] Declarations =
    [
        Decl("list_dir", "Список файлов и папок", ("path", "Путь к папке", false)),
        Decl("read_file", "Прочитать текстовый файл", ("path", "Путь к файлу", true)),
        Decl("write_file", "Записать текстовый файл", ("path", "Путь", true), ("content", "Содержимое", true)),
        Decl("run_powershell", "Выполнить команду PowerShell", ("command", "Команда", true)),
        Decl("screenshot", "Скриншот экрана в PNG", ("path", "Куда сохранить", false)),
        Decl("browser_open", "Открыть URL в браузере Playwright", ("url", "URL", true)),
        Decl("browser_close", "Закрыть браузер Playwright"),
        Decl("type_text", "Набрать текст клавиатурой", ("text", "Текст", true)),
        Decl("mouse_click", "Клик мышью", ("x", "X", true), ("y", "Y", true)),
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
