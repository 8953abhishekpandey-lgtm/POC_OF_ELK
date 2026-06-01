import { Component } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="app-shell">
      <!-- Sidebar -->
      <aside class="sidebar">
        <div class="sidebar-logo">
          <div class="logo-icon">⚡</div>
          <div class="logo-text">
            <span class="logo-title">ELK Monitor</span>
            <span class="logo-sub">Error Intelligence</span>
          </div>
        </div>

        <nav class="sidebar-nav">
          <a routerLink="/dashboard" routerLinkActive="active" class="nav-item">
            <span class="nav-icon">📊</span>
            <span>Dashboard</span>
          </a>
          <a routerLink="/logs" routerLinkActive="active" class="nav-item">
            <span class="nav-icon">📋</span>
            <span>Log Explorer</span>
          </a>
        </nav>

        <div class="sidebar-footer">
          <div class="status-dot"></div>
          <span>POC v1.0</span>
        </div>
      </aside>

      <!-- Main Content -->
      <main class="main-content">
        <router-outlet />
      </main>
    </div>
  `,
  styles: []
})
export class AppComponent {}
