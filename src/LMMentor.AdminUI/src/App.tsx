import { useEffect, useState } from 'react';
import { getCurrentUser } from './api/auth';
import { DashboardPage } from './pages/DashboardPage';
import { LoginPage } from './pages/LoginPage';

type AuthState = 'checking' | 'authenticated' | 'unauthenticated';

export default function App ()
{
  const [ state, setState ] = useState<AuthState>( 'checking' );

  useEffect( () =>
  {
    getCurrentUser()
      .then( () => setState( 'authenticated' ) )
      .catch( () => setState( 'unauthenticated' ) );
  }, [] );

  if ( state === 'checking' )
  {
    return <div className="login-page"><p>Carregando…</p></div>;
  }

  return state === 'authenticated' ? <DashboardPage /> : <LoginPage />;
}
