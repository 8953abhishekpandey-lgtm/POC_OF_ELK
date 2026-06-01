import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'dashboard',
    pathMatch: 'full'
  },
  {
    path: 'dashboard',
    loadComponent: () =>
      import('./dashboard/dashboard.component').then(m => m.DashboardComponent)
  },
  {
    path: 'logs',
    loadComponent: () =>
      import('./logs/log-table/log-table.component').then(m => m.LogTableComponent)
  },
  {
    path: '**',
    redirectTo: 'dashboard'
  }
];
