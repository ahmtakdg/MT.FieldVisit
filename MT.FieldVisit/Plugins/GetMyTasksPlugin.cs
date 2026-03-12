// ==============================
// Plugins/GetMyTasksPlugin.cs
// Custom Action: mt_GetMyTasks
// Input:  UserEmail (string)
// Output: TasksJson (string) — JSON array of task objects
// ==============================
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace MT.FieldVisit.Plugins
{
    public class GetMyTasksPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);

            try
            {
                // --- Input ---
                if (!context.InputParameters.Contains("mt_UserEmail"))
                    throw new InvalidPluginExecutionException("UserEmail input parameter is required.");

                var userEmail = context.InputParameters["mt_UserEmail"] as string;
                if (string.IsNullOrWhiteSpace(userEmail))
                    throw new InvalidPluginExecutionException("UserEmail cannot be empty.");

                tracing.Trace("GetMyTasks: UserEmail = {0}", userEmail);

                // --- 1. Find SystemUser by email ---
                var userQuery = new QueryExpression("systemuser")
                {
                    ColumnSet = new ColumnSet("systemuserid"),
                    TopCount = 1
                };
                userQuery.Criteria.AddCondition("internalemailaddress", ConditionOperator.Equal, userEmail);

                var userResult = service.RetrieveMultiple(userQuery);
                if (userResult.Entities.Count == 0)
                    throw new InvalidPluginExecutionException($"No user found with email: {userEmail}");

                var userId = userResult.Entities[0].Id;
                tracing.Trace("GetMyTasks: UserId = {0}", userId);

                // --- 2. Find Team IDs where user is a member ---
                var teamQuery = new QueryExpression("team")
                {
                    ColumnSet = new ColumnSet("teamid")
                };
                var teamMemberLink = teamQuery.AddLink("teammembership", "teamid", "teamid");
                teamMemberLink.LinkCriteria.AddCondition("systemuserid", ConditionOperator.Equal, userId);

                var teamResult = service.RetrieveMultiple(teamQuery);
                var teamIds = new List<Guid>();
                foreach (var team in teamResult.Entities)
                    teamIds.Add(team.Id);

                tracing.Trace("GetMyTasks: Found {0} teams", teamIds.Count);

                // --- 3. Query Tasks owned by user OR any of user's teams ---
                var taskQuery = new QueryExpression("task")
                {
                    ColumnSet = new ColumnSet(
                        "activityid",
                        "subject",
                        "scheduledstart",
                        "scheduledend",
                        "regardingobjectid",
                        "ownerid",
                        "statecode",
                        "statuscode"
                    )
                };

                taskQuery.Criteria.FilterOperator = LogicalOperator.And;
                taskQuery.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

                // ownerid = user OR ownerid IN (teams)
                var ownerFilter = new FilterExpression(LogicalOperator.Or);
                ownerFilter.AddCondition("ownerid", ConditionOperator.Equal, userId);

                if (teamIds.Count > 0)
                {
                    var teamCondition = new ConditionExpression("ownerid", ConditionOperator.In);
                    foreach (var teamId in teamIds)
                        teamCondition.Values.Add(teamId);
                    ownerFilter.Conditions.Add(teamCondition);
                }

                taskQuery.Criteria.AddFilter(ownerFilter);

                var taskOrders = new OrderExpression("scheduledstart", OrderType.Ascending);
                taskQuery.Orders.Add(taskOrders);

                var taskResult = service.RetrieveMultiple(taskQuery);
                tracing.Trace("GetMyTasks: Found {0} tasks", taskResult.Entities.Count);

                // --- 4. Serialize to JSON ---
                var json = SerializeTasksToJson(taskResult.Entities);

                context.OutputParameters["mt_TasksJson"] = json;
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tracing.Trace(ex.ToString());
                throw new InvalidPluginExecutionException("GetMyTasks error: " + ex.Message);
            }
        }

        private static string SerializeTasksToJson(DataCollection<Entity> tasks)
        {
            var sb = new StringBuilder();
            sb.Append("[");

            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (i > 0) sb.Append(",");

                sb.Append("{");
                sb.AppendFormat("\"activityid\":\"{0}\"", t.Id);
                sb.AppendFormat(",\"subject\":{0}", JsonString(t.GetAttributeValue<string>("subject")));

                var start = t.GetAttributeValue<DateTime?>("scheduledstart");
                sb.AppendFormat(",\"scheduledstart\":{0}", start.HasValue ? $"\"{start.Value:o}\"" : "null");

                var end = t.GetAttributeValue<DateTime?>("scheduledend");
                sb.AppendFormat(",\"scheduledend\":{0}", end.HasValue ? $"\"{end.Value:o}\"" : "null");

                var regarding = t.GetAttributeValue<EntityReference>("regardingobjectid");
                if (regarding != null)
                {
                    sb.AppendFormat(",\"regardingobjectid_name\":{0}", JsonString(regarding.Name));
                    sb.AppendFormat(",\"regardingobjectid_id\":\"{0}\"", regarding.Id);
                    sb.AppendFormat(",\"regardingobjectid_type\":\"{0}\"", regarding.LogicalName);
                }
                else
                {
                    sb.Append(",\"regardingobjectid_name\":null,\"regardingobjectid_id\":null,\"regardingobjectid_type\":null");
                }

                var owner = t.GetAttributeValue<EntityReference>("ownerid");
                if (owner != null)
                {
                    sb.AppendFormat(",\"ownerid_name\":{0}", JsonString(owner.Name));
                    sb.AppendFormat(",\"ownerid_id\":\"{0}\"", owner.Id);
                }
                else
                {
                    sb.Append(",\"ownerid_name\":null,\"ownerid_id\":null");
                }

                sb.AppendFormat(",\"statecode\":{0}", t.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0);
                sb.AppendFormat(",\"statuscode\":{0}", t.GetAttributeValue<OptionSetValue>("statuscode")?.Value ?? 1);

                sb.Append("}");
            }

            sb.Append("]");
            return sb.ToString();
        }

        private static string JsonString(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        }
    }
}
