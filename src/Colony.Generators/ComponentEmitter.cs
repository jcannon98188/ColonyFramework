using System.Text;

namespace Colony.Generators;

/// <summary>
/// Emits the generated half of a <c>[Component]</c> partial struct: the <c>IComponent&lt;T&gt;</c> layout with one
/// <c>ComponentField</c> per instance field (offsets measured with <c>Unsafe.ByteOffset</c>, no reflection), and
/// value equality so plain data structs satisfy CA1815 without hand-written boilerplate.
/// </summary>
internal static class ComponentEmitter
{
    private const string Unsafe = "global::System.Runtime.CompilerServices.Unsafe";

    public static string HintName(ComponentModel model) => model.Placement.HintPrefix + ".Component.g.cs";

    public static string Emit(ComponentModel model)
    {
        var w = new SourceWriter();
        int opened = w.OpenPlacement(model.Placement, $"[Component] on {model.FullName.Substring("global::".Length)}");
        string self = model.FullName;

        w.Line("/// <summary>Generated Component identity (spec section 4.1): the stable name <c>" + Escape(model.StableName) + "</c> and its field-level layout.</summary>");
        w.Open($"partial struct {model.Name} : global::Colony.IComponent<{self}>" + (model.EmitEquality ? $", global::System.IEquatable<{self}>" : string.Empty));

        w.Line($"private static readonly global::Colony.ComponentLayout _colonyLayout = ColonyBuildLayout();");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Line($"static global::Colony.ComponentLayout global::Colony.IComponent<{self}>.Layout => _colonyLayout;");
        w.Line();
        w.Open("private static global::Colony.ComponentLayout ColonyBuildLayout()");
        if (model.Fields.Count > 0)
        {
            w.Line($"{self} probe = default;");
            w.Line($"ref byte start = ref {Unsafe}.As<{self}, byte>(ref probe);");
        }

        w.Line("return new global::Colony.ComponentLayout(");
        w.Line($"    {SymbolHelpers.Literal(model.StableName)},");
        w.Line($"    global::Colony.ComponentOptions.{(model.NonFragmenting ? "NonFragmenting" : "None")},");
        if (model.Fields.Count == 0)
        {
            w.Line("    global::System.Collections.Immutable.ImmutableArray<global::Colony.ComponentField>.Empty);");
        }
        else
        {
            w.Line("    global::System.Collections.Immutable.ImmutableArray.Create(");
            for (int i = 0; i < model.Fields.Count; i++)
            {
                var field = model.Fields[i];
                string offset = $"(int){Unsafe}.ByteOffset(ref start, ref {Unsafe}.As<{field.TypeCode}, byte>(ref {Unsafe}.AsRef(in probe.{field.Name})))";
                string size = $"{Unsafe}.SizeOf<{field.TypeCode}>()";
                string end = i == model.Fields.Count - 1 ? "));" : ",";
                w.Line($"        new global::Colony.ComponentField({SymbolHelpers.Literal(field.Name)}, {SymbolHelpers.Literal(field.StableTypeName)}, {offset}, {size}){end}");
            }
        }

        w.Close();

        if (model.EmitEquality)
        {
            EmitEquality(w, model);
        }

        w.Close();
        w.CloseAll(opened);
        return w.ToString();
    }

    private static void EmitEquality(SourceWriter w, ComponentModel model)
    {
        string self = model.FullName;
        w.Line();
        w.Line("/// <summary>Field-wise equality (generated).</summary>");
        var equals = new StringBuilder();
        if (model.Fields.Count == 0)
        {
            equals.Append("true");
        }
        else
        {
            for (int i = 0; i < model.Fields.Count; i++)
            {
                if (i > 0)
                {
                    equals.Append(" && ");
                }

                equals.Append(model.Fields[i].Name).Append(".Equals(other.").Append(model.Fields[i].Name).Append(')');
            }
        }

        w.Line($"public bool Equals({self} other) => {equals};");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Line($"public override bool Equals(object? obj) => obj is {self} other && Equals(other);");
        w.Line();
        w.Line("/// <inheritdoc />");
        w.Open("public override int GetHashCode()");
        w.Line("var hash = new global::System.HashCode();");
        foreach (var field in model.Fields)
        {
            w.Line($"hash.Add({field.Name});");
        }

        w.Line("return hash.ToHashCode();");
        w.Close();
        w.Line();
        w.Line($"public static bool operator ==({self} left, {self} right) => left.Equals(right);");
        w.Line();
        w.Line($"public static bool operator !=({self} left, {self} right) => !left.Equals(right);");
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
