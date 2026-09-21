import { useEffect, useState } from 'react';
import { HashRouter, Route, Routes } from 'react-router-dom';
import { Center, Loader } from '@mantine/core';
import { getCurrentUser } from './api/auth';
import { AdminDataProvider } from './context/AdminDataContext';
import { AdminLayout } from './pages/AdminLayout';
import { AddEndpointPage } from './pages/AddEndpointPage';
import { AddKeyPage } from './pages/AddKeyPage';
import { DashboardPage } from './pages/DashboardPage';
import { EndpointDetailPage } from './pages/EndpointDetailPage';
import { EndpointsPage } from './pages/EndpointsPage';
import { KeysPage } from './pages/KeysPage';
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
    return (
      <Center h="100vh">
        <Loader />
      </Center>
    );
  }

  if ( state === 'unauthenticated' )
  {
    return <LoginPage />;
  }

  // HashRouter: as rotas ficam sob /admin/#/... e o backend só precisa servir index.html em /admin.
  return (
    <HashRouter>
      <AdminDataProvider>
        <Routes>
          <Route element={ <AdminLayout /> }>
            <Route index element={ <DashboardPage /> } />
            <Route path="endpoints" element={ <EndpointsPage /> } />
            <Route path="endpoints/new" element={ <AddEndpointPage /> } />
            <Route path="endpoints/:id" element={ <EndpointDetailPage /> } />
            <Route path="keys" element={ <KeysPage /> } />
            <Route path="keys/new" element={ <AddKeyPage /> } />
          </Route>
        </Routes>
      </AdminDataProvider>
    </HashRouter>
  );
}
