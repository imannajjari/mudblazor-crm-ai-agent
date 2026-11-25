using System.Text.Json;

namespace MudBlazorCrmApp.Services.Agent;

public static class AgentToolSchemas
{
    private static readonly JsonElement[] Phase2Tools = BuildPhase2Tools();

    public static JsonElement[] GetTools() => Phase2Tools;

    private static JsonElement[] BuildPhase2Tools()
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
                    "enum": ["dashboard_kpis", "customers", "contacts", "leads", "opportunities", "sales", "support_cases", "activities", "todo_tasks"],
                    "description": "Data source name."
                  },
                  "top": {
                    "type": "integer",
                    "description": "Max rows to return (1-25, default 10)."
                  },
                  "skip": {
                    "type": "integer",
                    "description": "Rows to skip for paging (default 0)."
                  },
                  "search": {
                    "type": "string",
                    "description": "Optional case-insensitive substring filter."
                  },
                  "status": {
                    "type": "string",
                    "description": "Optional exact Status (or Stage for opportunities) filter."
                  }
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
                "properties": {
                  "question": {
                    "type": "string",
                    "description": "Specific self-contained question."
                  }
                },
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
                "properties": {
                  "title": {
                    "type": "string",
                    "description": "Section title or keyword."
                  }
                },
                "required": ["title"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "scan_screen",
              "description": "Read a fresh JSON snapshot of the current screen (fields, grids, tabs, actions, dialog state, validation errors). Use on an unknown screen or after navigation.",
              "parameters": {
                "type": "object",
                "properties": {},
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "go_to_page",
              "description": "Navigate to another CRM page by route or menu name.",
              "parameters": {
                "type": "object",
                "properties": {
                  "name": {
                    "type": "string",
                    "description": "Page name, Persian or English label, or route path."
                  }
                },
                "required": ["name"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "select_tab",
              "description": "Switch to a tab on the current screen by its id from the latest snapshot.",
              "parameters": {
                "type": "object",
                "properties": {
                  "id": {
                    "type": "string",
                    "description": "Tab id from the latest snapshot."
                  }
                },
                "required": ["id"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "look_at_screen",
              "description": "Read visible screen text when the compact snapshot lacks details. Ask one specific question.",
              "parameters": {
                "type": "object",
                "properties": {
                  "question": {
                    "type": "string",
                    "description": "One specific question about what is visible on the screen."
                  },
                  "mode": {
                    "type": "string",
                    "enum": ["text"],
                    "description": "Inspection mode."
                  }
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
                "properties": {
                  "term": {
                    "type": "string",
                    "description": "Term or caption to look up."
                  }
                },
                "required": ["term"],
                "additionalProperties": false
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "ask_user",
              "description": "Ask the user one short clarifying question when a required choice or object is ambiguous.",
              "parameters": {
                "type": "object",
                "properties": {
                  "question": {
                    "type": "string",
                    "description": "One concise question for the user."
                  }
                },
                "required": ["question"],
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
                "properties": {
                  "summary": {
                    "type": "string",
                    "description": "Concise Markdown summary of findings and where the user is left."
                  }
                },
                "required": ["summary"],
                "additionalProperties": false
              }
            }
          }
        ]
        """;

        return JsonSerializer.Deserialize<JsonElement[]>(json)
               ?? throw new InvalidOperationException("Failed to parse Phase 2 tool schemas.");
    }
}
