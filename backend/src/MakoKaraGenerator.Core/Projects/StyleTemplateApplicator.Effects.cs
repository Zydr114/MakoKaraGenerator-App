using System.Security.Cryptography;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

public static partial class StyleTemplateApplicator
{
    private readonly record struct Owner(StyleTemplateKind Kind, string Id);
    private static Owner[] Owners(Project project, StyleTemplateKind kind, string? line, string? token, bool all)
        => [.. project.Lyrics.AllLines.Where(l => all || l.Id == line).SelectMany(l => kind == StyleTemplateKind.Line
            ? [new Owner(kind, l.Id)] : l.Tokens.Where(t => all || t.Id == token).Select(t => new Owner(kind, t.Id)))];
    private static (ProjectEffectBindingKind Kind, string Id, string Recipe) Key(ProjectEffectInstance instance)
        => (instance.Binding.Kind, instance.Binding.Ids[0], instance.TemplateOrigin!.RecipeId);
    private static Project ApplyRecipes(Project project, StyleTemplate template, Owner[] owners)
    {
        var selected = owners.ToHashSet(); var effects = project.Effects;
        foreach (var package in template.Packages) effects = ProjectEffectsEditor.Register(effects, package);
        var existing = effects.Instances.Where(i => i.TemplateOrigin?.TemplateId == template.Id).ToDictionary(Key);
        List<ProjectEffectInstance> desired = [];
        foreach (var owner in owners)
        foreach (var recipe in template.EffectRecipes)
        {
            string[] targets = recipe.Target == TemplateEffectTarget.Line ? [owner.Id]
                : owner.Kind == StyleTemplateKind.Token ? [owner.Id] : [.. project.Lyrics.AllLines.Single(l => l.Id == owner.Id).Tokens.Select(t => t.Id)];
            var kind = recipe.Target == TemplateEffectTarget.Line ? ProjectEffectBindingKind.Lines : ProjectEffectBindingKind.Tokens;
            foreach (string id in targets)
            {
                existing.TryGetValue((kind, id, recipe.RecipeId), out var old);
                var instance = old ?? new ProjectEffectInstance { Id = "fx:" + Guid.NewGuid().ToString("N"), Name = recipe.Name,
                    PackageId = recipe.PackageId, Revision = recipe.Revision, ExportId = recipe.ExportId,
                    Seed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) };
                desired.Add(instance with { Name = recipe.Name, PackageId = recipe.PackageId, Revision = recipe.Revision, ExportId = recipe.ExportId,
                    Binding = new(kind, ids: [id]), Parameters = recipe.Parameters, OverlayLayer = recipe.OverlayLayer,
                    TemplateOrigin = new(template.Id, recipe.RecipeId, owner.Kind, owner.Id) });
            }
        }
        var keys = desired.Select(Key).ToHashSet();
        bool Affected(ProjectEffectInstance i) => i.TemplateOrigin is { } origin && origin.TemplateId == template.Id
            && (selected.Contains(new(origin.OwnerKind, origin.OwnerId)) || keys.Contains(Key(i)));
        int first = -1;
        for (int index = 0; index < effects.Instances.Length; index++) if (Affected(effects.Instances[index])) { first = index; break; }
        var unaffected = effects.Instances.Where(i => !Affected(i)).ToList();
        int insertion = first < 0 ? unaffected.Count : effects.Instances.Take(first).Count(i => !Affected(i));
        unaffected.InsertRange(insertion, desired);
        return ProjectEffectsEditor.Replace(project, new(effects.Packages, unaffected, effects.Budgets));
    }
    private static bool OwnerChanged(Project before, Project after, string templateId, Owner owner)
    {
        var oldStyle = owner.Kind == StyleTemplateKind.Line ? before.Lyrics.AllLines.Single(l => l.Id == owner.Id).StyleOverride
            : before.Lyrics.AllLines.SelectMany(l => l.Tokens).Single(t => t.Id == owner.Id).StyleOverride;
        var newStyle = owner.Kind == StyleTemplateKind.Line ? after.Lyrics.AllLines.Single(l => l.Id == owner.Id).StyleOverride
            : after.Lyrics.AllLines.SelectMany(l => l.Tokens).Single(t => t.Id == owner.Id).StyleOverride;
        bool Matches(ProjectEffectInstance i) => i.TemplateOrigin is { } o && o.TemplateId == templateId && o.OwnerKind == owner.Kind && o.OwnerId == owner.Id;
        return oldStyle != newStyle || !before.Effects.Instances.Where(Matches).SequenceEqual(after.Effects.Instances.Where(Matches));
    }
    /// <summary>明确清除此模板在所选行／元素的效果，静态样式及其他实例保持。</summary>
    public static bool TryRemoveEffects(Project project, string templateId, StyleTemplateKind kind, string? lineId, string? tokenId, bool all,
        out StyleTemplateApplyPreview preview, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var owners = Owners(project, kind, lineId, tokenId, all); reason = string.Empty; preview = new(project, 0, 0);
        if (owners.Length == 0) { reason = "没有所选歌词行或元素。"; return false; }
        var selected = owners.ToHashSet();
        var remaining = project.Effects.Instances.Where(i => i.TemplateOrigin is not { } o || o.TemplateId != templateId || !selected.Contains(new(o.OwnerKind, o.OwnerId)));
        var result = ProjectEffectsEditor.Replace(project, new(project.Effects.Packages, remaining, project.Effects.Budgets));
        preview = new(result, owners.Length, owners.Count(o => OwnerChanged(project, result, templateId, o))); return true;
    }
}
