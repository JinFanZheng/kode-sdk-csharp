import { Plus, MessageSquare, Trash2, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import { useChat } from "@/contexts/ChatContext";
import { cn } from "@/lib/utils";
import { ThemeToggle } from "@/components/ThemeToggle";

export function SessionList() {
  const {
    sessions,
    currentSession,
    createSession,
    selectSession,
    deleteSession,
  } = useChat();

  const handleDelete = (e: React.MouseEvent, sessionId: string) => {
    e.stopPropagation();
    if (confirm("Are you sure you want to delete this chat?")) {
      deleteSession(sessionId);
    }
  };

  return (
    <div className="flex h-full flex-col border-r border-border bg-card">
      {/* Header */}
      <div className="flex items-center justify-between border-b border-border p-4">
        <div className="flex items-center gap-2">
          <h2 className="text-3xl font-semibold text-primary">Ask GLM</h2>
        </div>
        <div className="flex items-center gap-1">
          <ThemeToggle />
          <Button
            variant="ghost"
            size="icon"
            onClick={createSession}
            title="New Chat"
            className="hover:bg-accent"
          >
            <Plus className="h-5 w-5" />
          </Button>
        </div>
      </div>

      {/* Session List */}
      <ScrollArea className="flex-1">
        <div className="space-y-1 p-2">
          {sessions.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-8 text-muted-foreground">
              <MessageSquare className="mb-2 h-12 w-12 opacity-50" />
              <p className="text-sm">No chats yet</p>
              <p className="text-xs">Click + to start a new chat</p>
            </div>
          ) : (
            sessions.map((session) => (
              <div
                key={session.id}
                className={cn(
                  "group relative flex cursor-pointer items-center gap-3 rounded-lg p-3 transition-colors hover:bg-accent",
                  currentSession?.id === session.id && "bg-accent",
                )}
                onClick={() => selectSession(session.id)}
              >
                <MessageSquare className="h-4 w-4 shrink-0 text-primary" />
                <div className="flex-1 overflow-hidden">
                  <p className="truncate text-sm font-medium text-primary">
                    {session.title}
                  </p>
                  <p className="truncate text-xs text-muted-foreground">
                    {session.messages.length} messages
                  </p>
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  className="h-8 w-8 shrink-0 opacity-0 group-hover:opacity-100 hover:bg-destructive/20 hover:text-destructive transition-all duration-200"
                  onClick={(e) => handleDelete(e, session.id)}
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </div>
            ))
          )}
        </div>
      </ScrollArea>

      {/* Footer */}
      <div className="border-t border-border p-4">
        <p className="text-xs text-muted-foreground">Powered by Kode-SDK-C#</p>
      </div>
    </div>
  );
}
