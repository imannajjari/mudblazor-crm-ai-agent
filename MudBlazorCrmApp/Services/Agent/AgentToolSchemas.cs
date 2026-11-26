using System.Text.Json;

namespace MudBlazorCrmApp.Services.Agent;

public static class AgentToolSchemas
{
    private static readonly JsonElement[] Phase3Tools = BuildPhase3Tools();
    private static readonly JsonElement[] HowToTools = Phase3Tools
        .Where(t => t.TryGetProperty("function", out var fn) &&
                    fn.TryGetProperty("name", out var n) &&
                    n.GetString() is "read_data" or "ask_help" or "get_help_page"
                        or "lookup_term" or "ask_knowledge"
                        or "list_attachments" or "read_attachment" or "ask_attachment" or "finish")
        .ToArray();

    public static JsonElement[] GetTools() => Phase3Tools;
    public static JsonElement[] GetHowToTools() => HowToTools;

    private static JsonElement[] BuildPhase3Tools()
    {
        var readDataCatalog = AgentDataService.GetCatalogDescription().Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        var json = $$"""
        [
          {
            "type": "function",
            "function": {
              "name": "read_data",
              "description": "Read structured CRM data through the safe read-only catalog (with the user's permissions). Sources:\n{{readDataCatalog}}",
              "parameters": {
                "type": "object",
                "properties": {
                  "source": {
                    "type": "string",
                    "enum": ["dashboard_kpis", "customers", "contacts", "leads", "opportunities", "sales", "support_cases", "activities", "todo_tasks"]
                  },
                  "top": { "type": "integer" },
                  "skip": { "type": "integer" },
                  "search": { "type": "string" },
                  "status": { "type": "string" }
                },
                "required": ["source"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "ask_help",
              "description": "Search the CRM user guide for how-to instructions, workflows, or screen locations.",
              "parameters": {
                "type": "object",
                "properties": { "question": { "type": "string" } },
                "required": ["question"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "get_help_page",
              "description": "Fetch a full section from the CRM user guide by section title.",
              "parameters": {
                "type": "object",
                "properties": { "title": { "type": "string" } },
                "required": ["title"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "scan_screen",
              "description": "Read a fresh JSON snapshot of the current screen (fields, grids, tabs, actions, dialog state, errors).",
              "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "go_to_page",
              "description": "Navigate to another CRM page by route or menu name.",
              "parameters": {
                "type": "object",
                "properties": { "name": { "type": "string" } },
                "required": ["name"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "select_tab",
              "description": "Switch to a tab on the current screen by its id.",
              "parameters": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "set_field",
              "description": "Fill an input field by its id from the latest snapshot. Returns the read-back fieldValue.",
              "parameters": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "value": { "type": "string" },
                  "delegatedBy": { "type": "string", "description": "Set only when the user explicitly delegated choosing the value." }
                },
                "required": ["id", "value"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "open_combo",
              "description": "Open a MudSelect/combo dropdown by its field id to see lookupOptions.",
              "parameters": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "pick_option",
              "description": "Pick an option from an open combo dropdown by its 1-based index or exact text.",
              "parameters": {
                "type": "object",
                "properties": { "option": { "type": "string" } },
                "required": ["option"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "close_combo",
              "description": "Close an open combo dropdown without picking.",
              "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "click_action",
              "description": "Click a button or link by its id from the latest snapshot. Actions with risk 'save' or 'destructive' go through the user's permission gate.",
              "parameters": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "targetName": { "type": "string", "description": "For destructive actions, the exact record name/id being deleted." }
                },
                "required": ["id"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "read_grid_headers",
              "description": "List headers of visible tables or check whether a column exists.",
              "parameters": {
                "type": "object",
                "properties": { "find": { "type": "string" } },
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "select_grid_row",
              "description": "Click/select a grid row by its rowId.",
              "parameters": {
                "type": "object",
                "properties": { "rowId": { "type": "string" } },
                "required": ["rowId"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "clear_filters",
              "description": "Clear active search/filter inputs on the current grid or screen.",
              "parameters": { "type": "object", "properties": {}, "additionalProperties": false }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "sort_grid",
              "description": "Sort the visible grid by a column header name.",
              "parameters": {
                "type": "object",
                "properties": {
                  "column": { "type": "string" },
                  "direction": { "type": "string", "enum": ["asc", "desc"] }
                },
                "required": ["column"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "right_click",
              "description": "Right-click a grid row (rowId) or action/element (id) to open its context menu.",
              "parameters": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "look_at_screen",
              "description": "Read visible screen text when the compact snapshot lacks details.",
              "parameters": {
                "type": "object",
                "properties": {
                  "question": { "type": "string" },
                  "mode": { "type": "string", "enum": ["text"] }
                },
                "required": ["question"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "lookup_term",
              "description": "Look up a UI caption or domain term between Persian and English.",
              "parameters": {
                "type": "object",
                "properties": { "term": { "type": "string" } },
                "required": ["term"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "ask_user",
              "description": "Ask the user one short clarifying question when a required value or choice is missing.",
              "parameters": {
                "type": "object",
                "properties": {
                  "question": { "type": "string" },
                  "options": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["question"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "remember",
              "description": "Save a short verified UI fact (12-300 chars) to the shared knowledge base.",
              "parameters": {
                "type": "object",
                "properties": {
                  "topic": { "type": "string" },
                  "fact": { "type": "string" }
                },
                "required": ["topic", "fact"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "ask_knowledge",
              "description": "Query the shared knowledge base for verified UI facts.",
              "parameters": {
                "type": "object",
                "properties": { "question": { "type": "string" } },
                "required": ["question"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "correct_fact",
              "description": "Correct a previously shown knowledge fact by fact_id with a reason (>=10 chars).",
              "parameters": {
                "type": "object",
                "properties": {
                  "fact_id": { "type": "integer" },
                  "fact": { "type": "string" },
                  "reason": { "type": "string" }
                },
                "required": ["fact_id", "fact", "reason"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "delete_fact",
              "description": "Delete a previously shown knowledge fact by fact_id with a reason (>=10 chars).",
              "parameters": {
                "type": "object",
                "properties": {
                  "fact_id": { "type": "integer" },
                  "reason": { "type": "string" }
                },
                "required": ["fact_id", "reason"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "fact_did_not_help",
              "description": "Report that a shown knowledge fact_id did not help on this screen.",
              "parameters": {
                "type": "object",
                "properties": { "fact_id": { "type": "integer" } },
                "required": ["fact_id"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "list_attachments",
              "description": "List attached files for the current chat.",
              "parameters": {
                "type": "object",
                "properties": {},
                "required": [],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "read_attachment",
              "description": "Read paged rows or text from an attached file.",
              "parameters": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "sheet": { "type": "string" },
                  "fromRow": { "type": "integer" },
                  "rowCount": { "type": "integer" }
                },
                "required": ["id"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "ask_attachment",
              "description": "Ask a question about an attached file.",
              "parameters": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "question": { "type": "string" }
                },
                "required": ["id", "question"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "upload_file_to_screen",
              "description": "Upload a user-attached file into a file input on the current screen.",
              "parameters": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "attachmentId": { "type": "string" }
                },
                "required": ["id", "attachmentId"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "run_business_action",
              "description": "Execute a registered CRM business workflow action (e.g., open_pipeline_summary, open_overdue_tasks).",
              "parameters": {
                "type": "object",
                "properties": {
                  "action": { "type": "string" }
                },
                "required": ["action"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "finish",
              "description": "End the task and present the final Markdown summary to the user in the same language as the user's request.",
              "parameters": {
                "type": "object",
                "properties": { "summary": { "type": "string" } },
                "required": ["summary"],
                "additionalProperties": false
              }
            }
          }
        ]
        """;

        return JsonSerializer.Deserialize<JsonElement[]>(json)
               ?? throw new InvalidOperationException("Failed to parse Phase 3 tool schemas.");
    }
}
