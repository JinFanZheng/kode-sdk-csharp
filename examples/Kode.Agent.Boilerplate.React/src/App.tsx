import { ChatProvider } from "@/contexts/ChatContext";
import { SessionList } from "@/components/SessionList";
import { ChatPanel } from "@/components/ChatPanel";
import { useEffect } from "react";

function App() {
  useEffect(() => {
    // Initialize theme from localStorage, default to dark mode
    const savedTheme = localStorage.getItem("theme");

    if (savedTheme === "light") {
      document.documentElement.classList.remove("dark");
    } else {
      // Default to dark mode
      document.documentElement.classList.add("dark");
      if (!savedTheme) {
        localStorage.setItem("theme", "dark");
      }
    }
  }, []);

  return (
    <ChatProvider>
      <div className="flex h-screen overflow-hidden bg-background">
        {/* Left Sidebar - Session List */}
        <div className="w-80 shrink-0">
          <SessionList />
        </div>

        {/* Right Panel - Chat */}
        <div className="flex-1">
          <ChatPanel />
        </div>
      </div>
    </ChatProvider>
  );
}

export default App;
