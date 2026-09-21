import { useEffect, useState } from 'react';
import { getCurrentUser, logout, type AuthUser } from '../api/auth';

export function DashboardPage() {
  const [ user, setUser ] = useState<AuthUser | null>( null );

  useEffect( () => {
    getCurrentUser().then( setUser ).catch( () => setUser( null ) );
  }, [] );

  async function handleLogout() {
    await logout();
    window.location.href = '/admin/';
  }

  return (
    <div className="dashboard-page">
      <header className="dashboard-header">
        <h1>LMMentor</h1>
        <div className="dashboard-user">
          { user && <span>{ user.username }</span> }
          <button type="button" onClick={ handleLogout }>Sair</button>
        </div>
      </header>

      <main className="dashboard-content">
        <h2>Dashboard</h2>
      </main>
    </div>
  );
}
