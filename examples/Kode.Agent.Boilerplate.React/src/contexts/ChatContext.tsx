import { createContext, useContext, useState, useEffect, ReactNode } from 'react';
import type { Session, Message } from '@/types';

interface ChatContextType {
  sessions: Session[];
  currentSession: Session | null;
  createSession: () => void;
  selectSession: (sessionId: string) => void;
  deleteSession: (sessionId: string) => void;
  addMessage: (message: Message) => void;
  updateLastMessage: (content: string) => void;
  setCurrentSessionId: (sessionId: string | null) => void;
}

const ChatContext = createContext<ChatContextType | undefined>(undefined);

const STORAGE_KEY = 'kode-agent-sessions';

function loadSessionsFromStorage(): Session[] {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return stored ? JSON.parse(stored) : [];
  } catch {
    return [];
  }
}

function saveSessionsToStorage(sessions: Session[]) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(sessions));
  } catch (error) {
    console.error('Failed to save sessions:', error);
  }
}

export function ChatProvider({ children }: { children: ReactNode }) {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [currentSession, setCurrentSession] = useState<Session | null>(null);

  // Load sessions from localStorage on mount
  useEffect(() => {
    const stored = loadSessionsFromStorage();
    setSessions(stored);
    if (stored.length > 0) {
      setCurrentSession(stored[0]);
    }
  }, []);

  // Save sessions to localStorage whenever they change
  useEffect(() => {
    if (sessions.length > 0) {
      saveSessionsToStorage(sessions);
    }
  }, [sessions]);

  const createSession = () => {
    const newSession: Session = {
      id: crypto.randomUUID(),
      title: 'New Chat',
      messages: [],
      createdAt: Date.now(),
      updatedAt: Date.now(),
    };

    setSessions((prev) => [newSession, ...prev]);
    setCurrentSession(newSession);
  };

  const selectSession = (sessionId: string) => {
    const session = sessions.find((s) => s.id === sessionId);
    if (session) {
      setCurrentSession(session);
    }
  };

  const deleteSession = (sessionId: string) => {
    setSessions((prev) => {
      const filtered = prev.filter((s) => s.id !== sessionId);
      
      // If deleting current session, select the first available session
      if (currentSession?.id === sessionId) {
        setCurrentSession(filtered[0] || null);
      }
      
      return filtered;
    });
  };

  const addMessage = (message: Message) => {
    if (!currentSession) return;

    setSessions((prev) =>
      prev.map((session) => {
        if (session.id === currentSession.id) {
          const updatedMessages = [...session.messages, message];
          
          // Update title based on first user message
          let title = session.title;
          if (title === 'New Chat' && message.role === 'user') {
            title = message.content.slice(0, 50) + (message.content.length > 50 ? '...' : '');
          }

          const updatedSession = {
            ...session,
            title,
            messages: updatedMessages,
            updatedAt: Date.now(),
          };

          // Update current session reference
          setCurrentSession(updatedSession);
          return updatedSession;
        }
        return session;
      })
    );
  };

  const updateLastMessage = (content: string) => {
    if (!currentSession) return;

    setSessions((prev) =>
      prev.map((session) => {
        if (session.id === currentSession.id) {
          const messages = [...session.messages];
          const lastMessage = messages[messages.length - 1];
          
          if (lastMessage && lastMessage.role === 'assistant') {
            messages[messages.length - 1] = {
              ...lastMessage,
              content: lastMessage.content + content,
            };
          }

          const updatedSession = {
            ...session,
            messages,
            updatedAt: Date.now(),
          };

          setCurrentSession(updatedSession);
          return updatedSession;
        }
        return session;
      })
    );
  };

  const setCurrentSessionId = (sessionId: string | null) => {
    if (!sessionId || !currentSession) return;

    setSessions((prev) =>
      prev.map((session) => {
        if (session.id === currentSession.id) {
          const updatedSession = {
            ...session,
            id: sessionId, // Update with backend session ID
          };
          setCurrentSession(updatedSession);
          return updatedSession;
        }
        return session;
      })
    );
  };

  return (
    <ChatContext.Provider
      value={{
        sessions,
        currentSession,
        createSession,
        selectSession,
        deleteSession,
        addMessage,
        updateLastMessage,
        setCurrentSessionId,
      }}
    >
      {children}
    </ChatContext.Provider>
  );
}

export function useChat() {
  const context = useContext(ChatContext);
  if (!context) {
    throw new Error('useChat must be used within ChatProvider');
  }
  return context;
}
