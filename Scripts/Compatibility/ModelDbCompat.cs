using MegaCrit.Sts2.Core.Models;

namespace DrawAndGuessMod.Scripts.Compatibility;

internal static class ModelDbCompat
{
    public static bool IsMock(AbstractModel model)
    {
        Type type = model.GetType();
        string fullName = type.FullName ?? string.Empty;
        return model.Id.ToString().Contains("mock", StringComparison.OrdinalIgnoreCase)
               || type.Name.Contains("mock", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains(".Mocks.", StringComparison.OrdinalIgnoreCase)
               || fullName.Contains(".Mock.", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<TModel> GetAll<TModel>() where TModel : AbstractModel
    {
        foreach (Type type in ModelDb.AllAbstractModelSubtypes)
        {
            if (type.IsAbstract || !typeof(TModel).IsAssignableFrom(type))
            {
                continue;
            }

            TModel? model;
            try
            {
                model = ModelDb.GetByIdOrNull<TModel>(ModelDb.GetId(type));
            }
            catch
            {
                continue;
            }

            if (model != null)
            {
                yield return model;
            }
        }
    }
}
