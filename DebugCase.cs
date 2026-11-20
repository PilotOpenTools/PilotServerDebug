using System.Text.Json;
using System.Text.Json.Serialization;

namespace Debug;

/// <summary>
/// Кейс отладки (debug.json): куда подключаться, какую активность вызвать и с каким событием.
/// Админка выдаёт его на странице «Серверные модули» уже заполненным из автоматизаций контура;
/// аргументы командной строки перекрывают поля файла.
/// </summary>
public sealed class DebugCase
{
    /// <summary>Адрес базы: http://сервер:порт/база.</summary>
    public string Server { get; set; } = "";

    /// <summary>Логин разработчика в базе. Пароль - DEBUG_PASSWORD или вводится при запуске, в файл не пишется.</summary>
    public string? Login { get; set; }

    public int LicenseType { get; set; } = 103;

    /// <summary>Путь к *.servext.dll (относительно файла кейса). Обычно задаётся из launchSettings: $(TargetPath).</summary>
    public string? Module { get; set; }

    /// <summary>Имя активности (IServerActivity.Name). Пусто - если в модуле она одна.</summary>
    public string? Activity { get; set; }

    /// <summary>Объект-источник события (ev.Source).</summary>
    public Guid? SourceId { get; set; }

    /// <summary>Событие из сценария («when»): только для информации и для EventDate/ServerDateTime.</summary>
    public string? When { get; set; }

    /// <summary>params активности из сценария («then[].params»).</summary>
    public Dictionary<string, JsonElement> Params { get; set; } = new();

    /// <summary>params.sourceTypes триггера.</summary>
    public List<string> SourceTypes { get; set; } = new();

    /// <summary>Откуда кейс: автоматизации, в которых нашлась активность (для информации).</summary>
    public List<string> Automations { get; set; } = new();

    /// <summary>Контур сервера: "prod", "stage" или "test" (админка пишет сама). Нет поля - как раньше; для "prod" запись требует ввода слова prod.</summary>
    public string? Contour { get; set; }

    /// <summary>true - применить изменения на сервере после выполнения. По умолчанию - только показать.</summary>
    public bool Apply { get; set; }

    [JsonIgnore] public string? BaseDir { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DebugCase Load(string path)
    {
        var full = Path.GetFullPath(path);
        var c = JsonSerializer.Deserialize<DebugCase>(File.ReadAllText(full), Json)
                ?? throw new UsageException($"Пустой файл кейса {full}.");
        c.BaseDir = Path.GetDirectoryName(full);
        return c;
    }

    /// <summary>
    /// params в том виде, в каком их отдаёт Pilot-Server (AutomationScripts.JObjectExtensions.ToDictionary):
    /// Newtonsoft ToObject&lt;Dictionary&lt;string, object&gt;&gt; - строки, long, double, bool, DateTime;
    /// массивы - List&lt;object&gt; значений; вложенные объекты - такие же словари.
    /// </summary>
    public Dictionary<string, object> ParamsForActivity()
    {
        var raw = JsonSerializer.Serialize(Params, Json);
        return ServerParams(Newtonsoft.Json.Linq.JObject.Parse(raw));
    }

    private static Dictionary<string, object> ServerParams(Newtonsoft.Json.Linq.JObject obj)
    {
        var result = obj.ToObject<Dictionary<string, object>>()!;
        foreach (var key in result.Keys.ToList())
        {
            switch (result[key])
            {
                case Newtonsoft.Json.Linq.JArray array:
                    result[key] = array.Select(x => (x as Newtonsoft.Json.Linq.JValue)?.Value).ToList();
                    break;
                case Newtonsoft.Json.Linq.JObject nested:
                    result[key] = ServerParams(nested);
                    break;
            }
        }
        return result;
    }
}

public sealed class UsageException(string message) : Exception(message);
