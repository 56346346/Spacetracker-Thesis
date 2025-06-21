using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using InstantSync.Core.Delta;

namespace InstantSync.Core
{
    /// <summary>
    /// Observes element changes and writes deltas to a channel.
    /// </summary>
    public class ModelUpdater : IUpdater
    {
        private readonly AddInId _id;
        private readonly Guid _updaterId = new Guid("8E60D29D-7A74-4A8D-9905-7F3ECE3BF0CB");
        private readonly Func<Document, IElementConverter<ElementDto>> _converterFactory;
        public string GetUpdaterName() => "InstantSync-ModelUpdater";

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelUpdater"/> class.
        /// </summary>
        /// <param name="id">Application id.</param>
        /// <param name="converterFactory">Factory for converters.</param>
        public ModelUpdater(AddInId id, Func<Document, IElementConverter<ElementDto>> converterFactory)
        {
            _id = id;
            _converterFactory = converterFactory;
        }

        /// <inheritdoc/>
        public void Execute(UpdaterData data)
        {
            Document doc = data.GetDocument();
            var converter = _converterFactory(doc);

            foreach (ElementId id in data.GetModifiedElementIds())
            {
                Element e = doc.GetElement(id);
                if (e == null || !converter.CanConvert(e))
                {
                    continue;
                }

                Guid guid = GetOrCreateGuid(e);
                ElementDto dto = converter.ToDto(e, doc) with { Guid = guid };
                DeltaChannel.Instance.Writer.TryWrite(dto);
            }
        }

        /// <inheritdoc/>
        public string GetAdditionalInformation() => "Model delta updater";

        /// <inheritdoc/>
        public ChangePriority GetChangePriority() => ChangePriority.Any;

        /// <inheritdoc/>
        public UpdaterId GetUpdaterId() => new UpdaterId(_id, _updaterId);

        /// <inheritdoc/>
        public bool IsApplicable(Element element) => true;

        private static Guid GetOrCreateGuid(Element element)
        {
            Guid guid = Guid.NewGuid();
            var schema = Schema.Lookup(new Guid("D97D83E3-9BFA-4911-B398-6AF511C4AC91"));
            if (schema == null)
            {
                var builder = new SchemaBuilder(new Guid("D97D83E3-9BFA-4911-B398-6AF511C4AC91"));
                builder.AddSimpleField("Guid", typeof(string));
                builder.SetSchemaName("InstantSyncGuid");
                schema = builder.Finish();
            }

            Entity ent = element.GetEntity(schema);
            if (ent.IsValid())
            {
                string guidStr = ent.Get<string>(schema.GetField("Guid"));
                if (Guid.TryParse(guidStr, out var parsed))
                {
                    guid = parsed;
                }
            }
            else
            {
                ent = new Entity(schema);
                ent.Set("Guid", guid.ToString());
                element.SetEntity(ent);
            }

            return guid;
        }
    }
}
