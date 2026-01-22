import { ChatProvider } from '@/contexts/ChatContext';
import { SessionList } from '@/components/SessionList';
import { ChatPanel } from '@/components/ChatPanel';

function App() {
  return (
    <ChatProvider>
      <div className="flex h-screen overflow-hidden">
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
