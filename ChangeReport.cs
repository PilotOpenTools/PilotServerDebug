using System.Globalization;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;

namespace Debug;

/// <summary>Что модуль собирался записать: объекты, атрибуты (было / стало), родитель, связи, файлы, состояние.</summary>
internal static class ChangeReport
{
    public static int Print(IModifierBase modifier, IModifierBackend backend)
    {
        var changes = modifier.Changes.Values.ToList();
        if (changes.Count == 0)
        {
            Out.Info("Модуль не внёс изменений.");
            return 0;
        }

        var types = backend.GetTypes();
        Out.Title($"Изменения модуля: объектов {changes.Count}");
        foreach (var change in changes)
        {
            var o = change.Old;
            var n = change.New;
            var obj = n ?? o;
            var typeName = obj is not null && types.TryGetValue(obj.TypeId, out var t) ? $"{t.Title} ({t.Name})" : obj?.TypeId.ToString() ?? "?";
            Out.Line($"{(o is null ? "+ создание" : "~ изменение")}  {obj?.Id}  {typeName}");
            if (o is null || n is null)
            {
                if (n is not null)
                    foreach (var (key, value) in n.Attributes)
                        Out.Line($"    {key} = {Format(value)}");
                continue;
            }

            foreach (var key in o.Attributes.Keys.Union(n.Attributes.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                o.Attributes.TryGetValue(key, out var before);
                n.Attributes.TryGetValue(key, out var after);
                if (Equals(before, after))
                    continue;
                Out.Line($"    {key}: {Format(before)} / {Format(after)}");
            }
            if (o.ParentId != n.ParentId)
                Out.Line($"    родитель: {o.ParentId} / {n.ParentId}");
            if (o.StateInfo?.State != n.StateInfo?.State)
                Out.Line($"    состояние: {o.StateInfo?.State} / {n.StateInfo?.State}");

            var addedRelations = n.Relations.Where(r => !o.Relations.Any(x => x.Id == r.Id)).ToList();
            var removedRelations = o.Relations.Where(r => !n.Relations.Any(x => x.Id == r.Id)).ToList();
            foreach (var r in addedRelations)
                Out.Line($"    + связь {r.Type} «{r.Name}» / {r.TargetId}");
            foreach (var r in removedRelations)
                Out.Line($"    - связь {r.Type} «{r.Name}» / {r.TargetId}");

            if (n.PreviousFileSnapshots.Count != o.PreviousFileSnapshots.Count || !SameFiles(o.ActualFileSnapshot, n.ActualFileSnapshot))
            {
                var files = n.ActualFileSnapshot?.Files.Select(f => f.Name) ?? Enumerable.Empty<string>();
                Out.Line($"    файлы: новая версия «{n.ActualFileSnapshot?.Reason}» [{string.Join(", ", files)}]");
            }

            var addedAccess = n.Access.Count - o.Access.Count;
            if (addedAccess != 0)
                Out.Line($"    права доступа: записей {o.Access.Count} / {n.Access.Count}");
        }
        return changes.Count;
    }

    private static bool SameFiles(INFilesSnapshot? a, INFilesSnapshot? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Files.Select(f => f.Id).SequenceEqual(b.Files.Select(f => f.Id));
    }

    private static string Format(DValue? value)
    {
        if (value?.Value is null)
            return "∅";
        return value.Value switch
        {
            string s => $"\"{s}\"",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $" ({d.Kind})",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object>()) + "]",
            var v => v.ToString() ?? "",
        };
    }
}
