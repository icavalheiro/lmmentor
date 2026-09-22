import { request } from './http';
import type { UsageSummary } from '../types';

export function getUsageSummary ( days = 7 ): Promise<UsageSummary>
{
    return request( `/api/usage/summary?days=${ days }` );
}
