# Quick Start Script for Windows PowerShell

Write-Host "Installing dependencies..." -ForegroundColor Green
npm install

Write-Host "`nStarting development server..." -ForegroundColor Green
Write-Host "Frontend will run on http://localhost:3000" -ForegroundColor Cyan
Write-Host "Make sure the backend is running on http://localhost:5000" -ForegroundColor Yellow
Write-Host ""

npm run dev
