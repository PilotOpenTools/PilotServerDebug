namespace Debug;

/// <summary>Что делать перед записью на сервер: решение не зависит от консоли, поэтому проверяется тестами.</summary>
public enum WriteDecision
{
    /// <summary>Без --apply: только просмотр, ничего не спрашиваем.</summary>
    ViewOnly,
    /// <summary>Подтверждение уже дано ключом (--yes для test/stage, --yes-prod для прода).</summary>
    Proceed,
    /// <summary>Спросить y/N.</summary>
    AskYesNo,
    /// <summary>Спросить слово «prod»: y/N на прод не годится, это защита от случайного Enter и «--yes» в скрипте.</summary>
    AskProdWord,
}

public static class WriteGuard
{
    public const string ProdWord = "prod";

    public static bool IsProd(string? contour) => string.Equals(contour?.Trim(), "prod", StringComparison.OrdinalIgnoreCase);

    /// <summary>Контур не указан (старые файлы) или не prod - поведение прежнее: y/N, --yes отключает вопрос.</summary>
    public static WriteDecision Decide(string? contour, bool apply, bool yes, bool yesProd)
    {
        if (!apply)
            return WriteDecision.ViewOnly;
        if (IsProd(contour))
            return yesProd ? WriteDecision.Proceed : WriteDecision.AskProdWord;
        return yes ? WriteDecision.Proceed : WriteDecision.AskYesNo;
    }

    /// <summary>
    /// --server, отличный от server в кейсе, делает контур из файла недостоверным: адрес может вести на прод.
    /// Тогда контур считается prod (нужно слово). Кейс без контура остаётся как есть.
    /// </summary>
    public static string? EffectiveContour(string? contour, string? caseServer, string? overrideServer)
    {
        if (string.IsNullOrWhiteSpace(contour) || overrideServer is null)
            return contour;
        static string Norm(string? s) => (s ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        return Norm(caseServer) == Norm(overrideServer) ? contour : "prod";
    }

    public static bool IsProdWord(string? answer) => string.Equals(answer?.Trim(), ProdWord, StringComparison.Ordinal);
}
