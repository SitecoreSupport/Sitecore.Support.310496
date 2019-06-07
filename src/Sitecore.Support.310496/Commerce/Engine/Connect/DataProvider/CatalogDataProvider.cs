namespace Sitecore.Support.Commerce.Engine.Connect.DataProvider
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using Newtonsoft.Json.Linq;
  using Sitecore;
  using Sitecore.Collections;
  using Sitecore.Commerce.Core;
  using Sitecore.Commerce.Engine.Connect.DataProvider.Definitions;
  using Sitecore.Commerce.Engine.Connect.Fields;
  using Sitecore.Commerce.Engine.Connect.SitecoreDataProvider.Extensions;
  using Sitecore.Commerce.Plugin.Catalog;
  using Sitecore.Data;
  using Sitecore.Data.DataProviders;
  using Sitecore.Data.Items;
  using Sitecore.Data.Managers;
  using Sitecore.Diagnostics;
  using Sitecore.Commerce.Engine.Connect.DataProvider;

  public class CatalogDataProvider : Sitecore.Commerce.Engine.Connect.DataProvider.CatalogDataProvider
  {
        public override FieldList GetItemFields(ItemDefinition item, VersionUri version, CallContext context)
        {
            try
            {
                if (!CanProcessItem(item))
                {
                    // The item is not managed by this data provider.
                    return null;
                }

                if (item.TemplateID == KnownItemIds.CatalogFolderTemplateId)
                {
                    // Catalog Folder items require special handling when saving, but fields are managed as Sitecore items.
                    return null;
                }

                if (item.TemplateID == KnownItemIds.NavigationItemTemplateId)
                {
                    // Navigation Item items require special handling when retrieveing child IDs, but fields are managed as Sitecore items.
                    return null;
                }

                var template = TemplateManager.GetTemplate(item.TemplateID, ContentDb);
                if (template == null)
                {
                    return null;
                }

                var repository = new CatalogRepository(version.Language.CultureInfo.Name);

                var combinedEntityId = repository.GetEntityIdFromMappings(item.ID.Guid.ToString());

                // Validate the combinedEntityId
                if (string.IsNullOrEmpty(combinedEntityId))
                {
                    Log.Error($"Could not find the combined entity ID for Item ID {item.ID} with template ID {item.TemplateID}", this);
                    return null;
                }

                var itemId = item.ID.Guid.ToString();
                var variationId = string.Empty;

                if (item.TemplateID.ToString().Equals(SellableItemVariantTemplateId) || template.InheritsFrom(ID.Parse(SellableItemVariantTemplateId)))
                {
                    var parts = combinedEntityId.Split('|');

                    itemId = repository.GetSitecoreIdFromMappings(parts[0]);
                    variationId = parts[1];
                }

                context.Abort();

                var entity = repository.GetEntity(itemId, version.Version.Number);
                if (entity == null)
                {
                    return null;
                }

                var source = entity;
                var tokens = new List<JToken>();

                if (!string.IsNullOrEmpty(variationId))
                {
                    var variant =
                        entity["Components"]
                            .FirstOrDefault(x => x["@odata.type"].Value<string>().Contains("ItemVariationsComponent"))?["ChildComponents"]
                            .FirstOrDefault(x => x["Id"].Value<string>().Equals(variationId));

                    source = variant;
                    tokens.Add(variant);
                }

                tokens.Add(entity);

                var fields = new FieldList();
                var templateFields = template.GetFields();

                // Map data fields
                foreach (var field in templateFields.Where(ItemUtil.IsDataField))
                {
                    if (field.Name.Equals("VariationProperties"))
                    {
                        fields.Add(field.ID, repository.GetVariationProperties());
                    }
                    else if (field.Name.Equals("AreaServed"))
                    {
                        // TODO: Come up with a better solution to deal with those fields.

                        var property = tokens.GetEntityProperty(field.Name);

                        var location = property?.ToObject<GeoLocation>();
                        if (location != null)
                        {
                            var parts = new List<string>();

                            foreach (var part in new[] { location.City, location.Region, location.PostalCode })
                            {
                                if (!string.IsNullOrEmpty(part))
                                {
                                    parts.Add(part);
                                }
                            }

                            fields.Add(field.ID, string.Join(", ", parts));
                        }
                    }
                    else if (field.Name.Equals("ChildrenCategoryList") ||
                        field.Name.Equals("ChildrenSellableItemList"))
                    {
                        var mappedIdList = new List<string>();
                        var categorySitecoreIdList = tokens.GetEntityValue(field.Name).SplitPipedList();
                        foreach (var categorySitecoreId in categorySitecoreIdList)
                        {
                            // only retrieve path IDs that are children of the current item.
                            mappedIdList.AddRange(repository.GetPathIdsForSitecoreId(categorySitecoreId, itemId));
                        }

                        fields.Add(field.ID, string.Join("|", mappedIdList));
                    }
                    else if (field.Name.Equals("ParentCatalogList") ||
                        field.Name.Equals("ParentCategoryList"))
                    {
                        var mappedIdList = new List<string>();
                        var categorySitecoreIdList = tokens.GetEntityValue(field.Name).SplitPipedList();
                        foreach (var categorySitecoreId in categorySitecoreIdList)
                        {
                            mappedIdList.AddRange(repository.GetPathIdsForSitecoreId(categorySitecoreId));
                        }

                        mappedIdList = mappedIdList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        fields.Add(field.ID, string.Join("|", mappedIdList));
                    }
                    else
                    {
                        var value = tokens.GetEntityValue(field.Name);
                        if (value != null)
                        {
                            fields.Add(field.ID, value);
                        }
                    }
                }

                var components = source["Components"] ?? source["ChildComponents"];

                // Map external settings
                var externalSettingsCollection = components.GetExternalSettingsCollection(item.ID.Guid);
                Debug.Assert(externalSettingsCollection.ContainsKey(item.ID.Guid), $"externalSettingsCollection should contain an entry with key {item.ID.Guid}.");
                var externalSettings = externalSettingsCollection[item.ID.Guid];

                var languageSettings = new Dictionary<string, string>();
                if (externalSettings.ContainsKey(version.Language.Name))
                {
                    languageSettings = externalSettings[version.Language.Name];
                }

                var sharedSettings = new Dictionary<string, string>();
                if (externalSettings.ContainsKey("shared"))
                {
                    sharedSettings = externalSettings["shared"];
                }

                foreach (var language in new[] { languageSettings, sharedSettings })
                {
                    foreach (var setting in language)
                    {
                        var settingsField = templateFields.FirstOrDefault(x => x.Name.Equals(setting.Key));
                        if (settingsField != null)
                        {
                            fields.Add(settingsField.ID, setting.Value);
                        }
                    }
                }

                // Map relationships
                var relationships = components?.FirstOrDefault(x => x["@odata.type"].Value<string>().Contains("RelationshipsComponent"));
                if (relationships != null)
                {
                    var relationshipsValue = relationships["Relationships"];

                    foreach (var token in relationshipsValue)
                    {
                        var settingsField = templateFields.FirstOrDefault(x => x.Name.Equals(token["Name"].Value<string>()));
                        if (settingsField != null)
                        {
                            fields.Add(settingsField.ID, string.Join("|", token["RelationshipList"].Values<string>()));
                        }
                    }
                }

                // Set workflow status
                if (template.ID != CommerceConstants.KnownTemplateIds.CommerceProductVariantTemplate)
                {
                    var workflowComponent = components?.FirstOrDefault(x =>
                        x["@odata.type"].Value<string>().Contains("WorkflowComponent"));
                    if (workflowComponent != null)
                    {
                        var workflowId = workflowComponent["Workflow"]["EntityTarget"].Value<string>();
                        var workflowState = workflowComponent["CurrentState"].Value<string>();

                        if (!string.IsNullOrEmpty(workflowId) && !string.IsNullOrEmpty(workflowState))
                        {
                            var workflowSitecoreId = GuidUtils.GetDeterministicGuidString(workflowId);
                            var workflowStateSitecoreId =
                                GuidUtils.GetDeterministicGuidString($"{workflowId}|{workflowState}");

                            fields.Add(FieldIDs.Workflow, $"{{{workflowSitecoreId}}}");
                            fields.Add(FieldIDs.DefaultWorkflow, $"{{{workflowSitecoreId}}}");
                            fields.Add(FieldIDs.WorkflowState, $"{{{workflowStateSitecoreId}}}");
                        }
                    }
                }

                // Map item definitions (Catalog)
                var definitionsComponent = components?.FirstOrDefault(x => x["@odata.type"].Value<string>().Contains("ItemDefinitionsComponent"));
                if (definitionsComponent != null)
                {
                    var itemField = templateFields.FirstOrDefault(x => x.Name.Equals("ItemDefinitions"));
                    if (itemField != null)
                    {
                        var definitions = definitionsComponent["Definitions"]?.Values<string>();
                        if (definitions != null)
                        {
                            var definitionsValue = string.Join("\r\n", definitions);
                            fields.Add(itemField.ID, definitionsValue);
                        }
                    }
                }

                // Map item definitions (SellableItem)
                var catalogsComponent = components?.FirstOrDefault(x => x["@odata.type"].Value<string>().Contains("CatalogsComponent"));
                if (catalogsComponent != null)
                {
                    var catalogName = repository.GetCatalogName(item.ID);

                    var itemdefinitionsValue = catalogsComponent["ChildComponents"]?.FirstOrDefault(c => c["Name"].Value<string>() == catalogName)?["ItemDefinition"]?.Value<string>();
                    if (itemdefinitionsValue != null)
                    {
                        var itemField = templateFields.FirstOrDefault(x => x.Name.Equals("ItemDefinitions"));

                        if (itemField != null)
                        {
                            //fields.Add(itemField.ID, itemdefinitionsValue["ItemDefinition"].Value<string>());
                            fields.Add(itemField.ID, itemdefinitionsValue);
                        }
                    }
                }

                // Map the composer templates
                var entityViewComponent = components?.FirstOrDefault(x => x["@odata.type"].Value<string>().Contains("EntityViewComponent"))?["View"];
                if (entityViewComponent != null)
                {
                    var childViews = entityViewComponent["ChildViews"];
                    if (childViews != null)
                    {
                        foreach (var childView in childViews)
                        {
                            var properties = childView["Properties"];
                            foreach (var property in properties)
                            {
                                var itemField = templateFields.FirstOrDefault(x => x.Name.Equals(property["Name"].Value<string>()));

                                if (itemField != null)
                                {
                                    if (itemField.Type == "Checkbox")
                                    {
                                        if (property["Value"].Value<string>() == "true")
                                        {
                                            fields.Add(itemField.ID, "1");
                                        }
                                        else
                                        {
                                            fields.Add(itemField.ID, "0");
                                        }
                                    }
                                    else if (itemField.Type == "DateTime")
                                    {
                                        var dateString = property["Value"].Value<string>();
                                        if (dateString != null)
                                        {
                                            var dateTime = DateTime.Parse(dateString);

                                            // Raw value for DateTime field in Sitecore
                                            var isoDate = DateUtil.ToIsoDate(dateTime);
                                            fields.Add(itemField.ID, isoDate);
                                        }
                                    }
                                    else
                                    {
                                        fields.Add(itemField.ID, property["Value"].Value<string>());
                                    }
                                }
                            }
                        }
                    }
                }

                // Map standard fields
                fields.Add(FieldIDs.DisplayName, source["DisplayName"]?.Value<string>());
                fields.Add(FieldIDs.Created, entity.GetEntityValue("DateCreated"));
                fields.Add(FieldIDs.Updated, entity.GetEntityValue("DateUpdated"));
                fields.Add(FieldIDs.Security, "ar|Everyone|pe|+item:read|pd|+item:read|");
                //fields.Add(FieldIDs.Revision, GuidUtils.GetDeterministicGuidString(
                //    string.Format(
                //        CultureInfo.InvariantCulture,
                //        "{0}-{1}-{2}",
                //        entity["Id"]?.Value<string>(),
                //        entity["EntityVersion"]?.Value<string>(),
                //        entity["Version"]?.Value<string>())));

                var createdBy = entity.GetEntityValue("CreatedBy");
                var updatedBy = entity.GetEntityValue("UpdatedBy");

                if (!string.IsNullOrEmpty(createdBy))
                {
                    fields.Add(FieldIDs.CreatedBy, createdBy);

                    if (string.IsNullOrEmpty(updatedBy))
                    {
                        updatedBy = createdBy;
                    }
                }

                if (!string.IsNullOrEmpty(updatedBy))
                {
                    fields.Add(FieldIDs.UpdatedBy, updatedBy);
                }

                return fields;
            }
            catch (Exception ex)
            {
                var errorMsg = $"There was an error in GetItemFields. ItemDefinition ID: {item.ID} Template ID: {item.TemplateID}.\r\nError StackTrace: {ex.StackTrace}";
                Log.Error(errorMsg, this);
                return null;
            }
        }

    }
}