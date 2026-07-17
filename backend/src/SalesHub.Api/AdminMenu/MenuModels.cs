namespace SalesHub.Api.AdminMenu;

/// <summary>Impacto de una acción: High exige confirmación (sí/no) antes de ejecutar.</summary>
public enum Impact { Low, High }

/// <summary>Tipo de dato que pide un input de un form de hoja.</summary>
public enum InputType { Text, Number, Bool, Choice }

/// <summary>Una opción numerada (para inputs Choice y sub-menús dinámicos).</summary>
public sealed record Choice(string Label, string Value);

/// <summary>
/// Un input que el bot le pide al maestro antes de ejecutar la acción de una hoja.
/// Para <see cref="InputType.Choice"/>, <see cref="Options"/> arma la lista numerada en runtime
/// (puede pegarle a la API y usar los inputs ya juntados, ej. "qué pregunta" depende de "qué app").
/// </summary>
public sealed class InputField
{
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public InputType Type { get; init; } = InputType.Text;
    public Func<IReadOnlyDictionary<string, string>, AdminApiExecutor, CancellationToken, Task<List<Choice>>>? Options { get; init; }
}

/// <summary>
/// Qué hace una hoja: método + path (con {placeholders} que se rellenan de los inputs), los inputs
/// a pedir, el impacto, y opcionalmente un <see cref="Build"/> para armar el body/path a medida
/// (ej. traer el DTO completo con un GET, cambiar un campo y devolver el body del PUT).
/// </summary>
public sealed class ActionSpec
{
    public string Method { get; init; } = "POST";
    public string PathTemplate { get; init; } = "";
    public List<InputField> Inputs { get; init; } = new();
    public Impact Impact { get; init; } = Impact.Low;
    public string? ConfirmText { get; init; }
    public string? SuccessText { get; init; }

    /// <summary>
    /// Arma (path, body) a partir de los inputs juntados. Si es null: el path se rellena con los
    /// {placeholders} = inputs por nombre, y el body = diccionario de inputs (null para GET/DELETE).
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>, AdminApiExecutor, CancellationToken, Task<(string path, object? body)>>? Build { get; init; }
}

/// <summary>
/// Un nodo del árbol de menú. Rama = tiene <see cref="Children"/> (o <see cref="DynamicChildren"/>
/// que se arman en runtime, ej. la lista de productos). Hoja = tiene <see cref="Action"/>.
/// </summary>
public sealed class MenuNode
{
    public string Title { get; init; } = "";
    public string? Icon { get; init; }
    public List<MenuNode>? Children { get; init; }
    public Func<AdminApiExecutor, CancellationToken, Task<List<MenuNode>>>? DynamicChildren { get; init; }
    public ActionSpec? Action { get; init; }

    public bool IsLeaf => Action is not null;
    public string Label => string.IsNullOrEmpty(Icon) ? Title : $"{Icon} {Title}";

    public async Task<List<MenuNode>> ChildrenAsync(AdminApiExecutor exec, CancellationToken ct)
        => Children ?? (DynamicChildren is not null ? await DynamicChildren(exec, ct) : new List<MenuNode>());
}
