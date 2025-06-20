using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using InstantSync.Core.Delta;
using Microsoft.Extensions.Logging;

namespace InstantSync.Core.Commands
{
    /// <summary>
    /// Imports delta packages into the Revit model.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PullCommand : IExternalCommand
    {
        private readonly IEnumerable<IElementConverter<ElementDto>> _converters;
        private readonly ILogger<PullCommand> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PullCommand"/> class.
        /// </summary>
        /// <param name="converters">Converters.</param>
        /// <param name="logger">Logger.</param>
        public PullCommand(IEnumerable<IElementConverter<ElementDto>> converters, ILogger<PullCommand> logger)
        {
            _converters = converters;
            _logger = logger;
        }

        /// <inheritdoc />
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            var dialog = new Autodesk.Revit.UI.Forms.OpenFileDialog
            {
                Multiselect = true,
                Filter = "JSON|*.json",
            };
            if (dialog.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            var packages = dialog.GetFileNames()
                .Select(file => JsonSerializer.Deserialize<DeltaPackage>(File.ReadAllText(file)))
                .Where(p => p != null)
                .ToList();

            var idMap = new Dictionary<Guid, ElementId>();
            using var tg = new TransactionGroup(uiDoc.Document, "Pull");
            tg.Start();
            foreach (var pkg in packages)
            {
                using var tx = new Transaction(uiDoc.Document, pkg!.PackageId.ToString());
                tx.Start();
                foreach (var dto in pkg!.Elements)
                {
                    var converter = _converters.FirstOrDefault(c => c.CanConvert(uiDoc.Document.GetElement(new ElementId(-1))));
                    if (converter == null)
                    {
                        continue;
                    }

                    try
                    {
                        converter.FromDto(dto, uiDoc.Document, idMap);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Import failed for {Guid}", dto.Guid);
                        tx.RollBack();
                        return Result.Failed;
                    }
                }
                tx.Commit();
            }
            tg.Assimilate();
            return Result.Succeeded;
        }
    }
}
