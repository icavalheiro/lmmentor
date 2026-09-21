import { createContext, useContext, useState, type Dispatch, type ReactNode, type SetStateAction } from 'react';
import { mockApiKeys, mockEndpoints, mockModels } from '../data/mockData';
import type { ApiEndpoint, ApiKey, Model } from '../types';

interface AdminData
{
    endpoints: ApiEndpoint[];
    models: Model[];
    keys: ApiKey[];
    setEndpoints: Dispatch<SetStateAction<ApiEndpoint[]>>;
    setModels: Dispatch<SetStateAction<Model[]>>;
    setKeys: Dispatch<SetStateAction<ApiKey[]>>;
}

const AdminDataContext = createContext<AdminData | null>( null );

export function AdminDataProvider ( { children }: { children: ReactNode; } )
{
    const [ endpoints, setEndpoints ] = useState<ApiEndpoint[]>( mockEndpoints );
    const [ models, setModels ] = useState<Model[]>( mockModels );
    const [ keys, setKeys ] = useState<ApiKey[]>( mockApiKeys );

    return (
        <AdminDataContext.Provider value={ { endpoints, models, keys, setEndpoints, setModels, setKeys } }>
            { children }
        </AdminDataContext.Provider>
    );
}

export function useAdminData ()
{
    const ctx = useContext( AdminDataContext );
    if ( !ctx )
    {
        throw new Error( 'useAdminData deve ser usado dentro de <AdminDataProvider>.' );
    }
    return ctx;
}
