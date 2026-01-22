# Kode.Agent.Boilerplate.React

A modern React frontend for the Kode.Agent.Boilerplate API, providing a ChatGPT-like interface for interacting with AI agents.

## Features

✅ **Modern Tech Stack**
- React 19+ with TypeScript
- Vite for fast development and building
- Tailwind CSS 4.1 for styling
- Shadcn UI components

✅ **Chat Interface**
- ChatGPT-like user interface
- Session management (create, select, delete)
- Real-time streaming responses
- Message history persistence in localStorage

✅ **OpenAI Compatible**
- Works with Kode.Agent.Boilerplate API
- Supports streaming and non-streaming responses
- Automatic session ID management

## Screenshots

The application features:
- **Left Sidebar**: Session list with create/delete functionality
- **Right Panel**: Chat interface with message history and input

## Prerequisites

- Node.js 18+ or later
- npm, yarn, or pnpm package manager
- Running instance of [Kode.Agent.Boilerplate](../Kode.Agent.Boilerplate) backend API

## Quick Start

### 1. Install Dependencies

```bash
cd examples/Kode.Agent.Boilerplate.React
npm install
```

Or with yarn:
```bash
yarn install
```

Or with pnpm:
```bash
pnpm install
```

### 2. Configure Backend API

The frontend is configured to proxy API requests to `http://localhost:5000` by default. If your backend runs on a different port, update the proxy configuration in `vite.config.ts`:

```typescript
export default defineConfig({
  // ...
  server: {
    port: 3000,
    proxy: {
      '/v1': {
        target: 'http://localhost:5000', // Change this to your backend URL
        changeOrigin: true,
      },
    },
  },
})
```

### 3. Start Development Server

```bash
npm run dev
```

The application will be available at `http://localhost:3000`.

### 4. Start Backend API

Make sure the Kode.Agent.Boilerplate backend is running:

```bash
cd ../Kode.Agent.Boilerplate
dotnet run
```

## Usage

1. **Create a New Chat**: Click the `+` button in the sidebar
2. **Send Messages**: Type your message and press Enter (or click Send button)
3. **Switch Chats**: Click on any session in the sidebar
4. **Delete Chats**: Hover over a session and click the trash icon

### Keyboard Shortcuts

- `Enter`: Send message
- `Shift + Enter`: New line in message

## Project Structure

```
Kode.Agent.Boilerplate.React/
├── public/                  # Static assets
├── src/
│   ├── components/          # React components
│   │   ├── ui/              # Shadcn UI components
│   │   ├── ChatPanel.tsx    # Main chat interface
│   │   └── SessionList.tsx  # Session list sidebar
│   ├── contexts/            # React contexts
│   │   └── ChatContext.tsx  # Chat state management
│   ├── lib/                 # Utility functions
│   │   └── utils.ts         # Helper utilities
│   ├── services/            # API services
│   │   └── api.ts           # Backend API client
│   ├── types/               # TypeScript types
│   │   └── index.ts         # Type definitions
│   ├── App.tsx              # Root component
│   ├── main.tsx             # Application entry point
│   └── index.css            # Global styles
├── index.html               # HTML template
├── package.json             # Dependencies
├── tsconfig.json            # TypeScript config
├── tailwind.config.ts       # Tailwind CSS config
├── vite.config.ts           # Vite config
└── components.json          # Shadcn UI config
```

## Configuration

### API Endpoint

The API endpoint is configured in `src/services/api.ts`. By default, it uses `/v1` which is proxied to the backend through Vite's dev server.

### Theme Customization

You can customize the color scheme by editing the CSS variables in `src/index.css`:

```css
:root {
  --background: 0 0% 100%;
  --foreground: 222.2 84% 4.9%;
  /* ... more variables */
}
```

## Building for Production

### Build the Application

```bash
npm run build
```

The build output will be in the `dist/` directory.

### Preview Production Build

```bash
npm run preview
```

### Deploy

The built application is a static website and can be deployed to any static hosting service:

- Vercel
- Netlify
- GitHub Pages
- AWS S3 + CloudFront
- Azure Static Web Apps

Make sure to configure the API proxy or update the API base URL to point to your production backend.

## Development

### Adding New Shadcn Components

You can add more Shadcn UI components as needed. For example, to add a dialog component:

```bash
npx shadcn@latest add dialog
```

### Code Style

The project uses:
- ESLint for linting
- TypeScript for type checking

Run checks:
```bash
npm run lint
```

## Features in Detail

### Session Management

- Sessions are stored in localStorage for persistence
- Each session has a unique ID that syncs with the backend
- Session titles are auto-generated from the first user message

### Message Streaming

The application uses Server-Sent Events (SSE) for real-time streaming responses:

1. User sends a message
2. Frontend sends request to `/v1/chat/completions` with `stream: true`
3. Backend streams response chunks
4. Frontend displays chunks in real-time

### Error Handling

- Network errors are caught and displayed in the chat
- Failed requests show error messages
- Sessions persist even if the backend is temporarily unavailable

## Troubleshooting

### Backend Connection Issues

If you see connection errors:

1. Make sure the backend is running on `http://localhost:5000`
2. Check the proxy configuration in `vite.config.ts`
3. Check browser console for CORS errors

### Session Not Persisting

- Sessions are stored in localStorage
- Check if localStorage is enabled in your browser
- Clear localStorage to reset: `localStorage.clear()`

### Build Errors

If you encounter build errors:

1. Delete `node_modules` and reinstall: `rm -rf node_modules && npm install`
2. Clear Vite cache: `rm -rf node_modules/.vite`
3. Check TypeScript errors: `npm run build`

## License

Same as the parent project.

## Related Projects

- [Kode.Agent.Boilerplate](../Kode.Agent.Boilerplate) - Backend API
- [Kode.Agent.Sdk](../../src/Kode.Agent.Sdk) - Core SDK
- [Shadcn UI](https://ui.shadcn.com/) - UI components
- [Vite](https://vite.dev/) - Build tool
