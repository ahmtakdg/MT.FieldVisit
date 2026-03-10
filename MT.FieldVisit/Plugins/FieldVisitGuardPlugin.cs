// ==============================
// Plugins/FieldVisitGuardPlugin.cs
// ==============================
using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using MT.FieldVisit.Constants;

namespace MT.FieldVisit.Plugins
{
    public class FieldVisitGuardPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);

            try
            {
                if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
                    return;

                if (!string.Equals(target.LogicalName, EntityNames.FieldVisit, StringComparison.OrdinalIgnoreCase))
                    return;

                if (context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase))
                {
                    if (!target.Contains("mt_userid"))
                    {
                        target["mt_userid"] = new EntityReference(
                            "systemuser",
                            context.InitiatingUserId
                        );
                    }

                    HandleCreate(service, target);
                    return;
                }

                if (context.MessageName.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    var pre = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : null;
                    if (pre == null)
                        throw new InvalidPluginExecutionException("PreImage is required for this plugin step.");

                    HandleUpdate(target, pre);
                    return;
                }
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace(ex.ToString());
                throw new InvalidPluginExecutionException("FieldVisit validation error: " + ex.Message);
            }
        }

        private static void HandleCreate(IOrganizationService service, Entity target)
        {
            var userRef = target.GetAttributeValue<EntityReference>(FieldVisitFields.User);
            if (userRef == null)
                throw new InvalidPluginExecutionException("Ziyareti Yapan (mt_userid) zorunludur.");

            var status = target.GetAttributeValue<OptionSetValue>(FieldVisitFields.Status)?.Value ?? FieldVisitStatus.CheckedIn;

            if (status != FieldVisitStatus.CheckedIn)
                throw new InvalidPluginExecutionException("Yeni kayıt yalnızca 'Check-In Yapıldı' durumuyla oluşturulabilir.");

            if (HasOpenCheckIn(service, userRef.Id))
                throw new InvalidPluginExecutionException("Açık check-in kaydı var. Önce check-out veya iptal edin.");

            if (!target.Attributes.Contains(FieldVisitFields.CheckInTime))
                target[FieldVisitFields.CheckInTime] = DateTime.UtcNow;

            if (!target.Attributes.Contains(FieldVisitFields.VisitDate))
                target[FieldVisitFields.VisitDate] = DateTime.UtcNow.Date;

            ValidateCoordinatesIfPresent(target, FieldVisitFields.CheckInLat, FieldVisitFields.CheckInLng);
        }

        private static void HandleUpdate(Entity target, Entity pre)
        {
            var oldStatus = pre.GetAttributeValue<OptionSetValue>(FieldVisitFields.Status)?.Value;
            var newStatus = target.GetAttributeValue<OptionSetValue>(FieldVisitFields.Status)?.Value ?? oldStatus;

            // If status is not being updated, just validate coords if they are being changed
            if (!target.Attributes.Contains(FieldVisitFields.Status))
            {
                ValidateCoordinatesIfPresent(target, FieldVisitFields.CheckInLat, FieldVisitFields.CheckInLng);
                ValidateCoordinatesIfPresent(target, FieldVisitFields.CheckOutLat, FieldVisitFields.CheckOutLng);
                return;
            }

            if (newStatus == null)
                throw new InvalidPluginExecutionException("Ziyaret Durumu (mt_status) boş olamaz.");

            // Check-Out
            if (newStatus.Value == FieldVisitStatus.CheckedOut)
            {
                if (oldStatus != FieldVisitStatus.CheckedIn)
                    throw new InvalidPluginExecutionException("Check-out yalnızca 'Check-In Yapıldı' durumundan yapılabilir.");

                var checkInTime = pre.GetAttributeValue<DateTime?>(FieldVisitFields.CheckInTime);
                if (checkInTime == null)
                    throw new InvalidPluginExecutionException("Check-in zamanı bulunamadı.");

                if (!target.Attributes.Contains(FieldVisitFields.CheckOutTime))
                    target[FieldVisitFields.CheckOutTime] = DateTime.UtcNow;

                var checkOutTime = (DateTime)target[FieldVisitFields.CheckOutTime];
                if (checkOutTime < checkInTime.Value)
                    throw new InvalidPluginExecutionException("Check-out zamanı, check-in zamanından küçük olamaz.");

                ValidateCoordinatesIfPresent(target, FieldVisitFields.CheckOutLat, FieldVisitFields.CheckOutLng);
                return;
            }

            // Cancel
            if (newStatus.Value == FieldVisitStatus.Cancelled)
            {
                if (oldStatus == FieldVisitStatus.CheckedOut)
                    throw new InvalidPluginExecutionException("Check-out yapılmış kayıt iptal edilemez.");

                var cancelReason =
                    target.GetAttributeValue<OptionSetValue>(FieldVisitFields.CancelReason)
                    ?? pre.GetAttributeValue<OptionSetValue>(FieldVisitFields.CancelReason);

                if (cancelReason == null)
                    throw new InvalidPluginExecutionException("İptal Nedeni zorunludur.");

                return;
            }

            // Keep to known statuses
            if (newStatus.Value != FieldVisitStatus.CheckedIn &&
                newStatus.Value != FieldVisitStatus.CheckedOut &&
                newStatus.Value != FieldVisitStatus.Cancelled)
                throw new InvalidPluginExecutionException("Geçersiz durum değeri.");
        }

        private static bool HasOpenCheckIn(IOrganizationService service, Guid userId)
        {
            var qe = new QueryExpression(EntityNames.FieldVisit)
            {
                ColumnSet = new ColumnSet(false),
                TopCount = 1
            };

            qe.Criteria.AddCondition(FieldVisitFields.User, ConditionOperator.Equal, userId);
            qe.Criteria.AddCondition(FieldVisitFields.Status, ConditionOperator.Equal, FieldVisitStatus.CheckedIn);

            var result = service.RetrieveMultiple(qe);
            return result?.Entities != null && result.Entities.Count > 0;
        }

        private static void ValidateCoordinatesIfPresent(Entity e, string latCol, string lngCol)
        {
            var hasLat = e.Attributes.Contains(latCol);
            var hasLng = e.Attributes.Contains(lngCol);

            if (!hasLat && !hasLng) return;

            if (hasLat && e[latCol] != null)
            {
                var lat = Convert.ToDecimal(e[latCol]);
                if (lat < -90m || lat > 90m)
                    throw new InvalidPluginExecutionException($"{latCol} değeri -90 ile 90 arasında olmalıdır.");
            }

            if (hasLng && e[lngCol] != null)
            {
                var lng = Convert.ToDecimal(e[lngCol]);
                if (lng < -180m || lng > 180m)
                    throw new InvalidPluginExecutionException($"{lngCol} değeri -180 ile 180 arasında olmalıdır.");
            }
        }
    }
}