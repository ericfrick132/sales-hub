import axios from 'axios';
import { useAuthStore } from './auth';

const base = import.meta.env.VITE_API_URL ?? '/api';

export const api = axios.create({ baseURL: base });

api.interceptors.request.use((config) => {
  const token = useAuthStore.getState().token;
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

api.interceptors.response.use(
  (r) => r,
  (err) => {
    if (err.response?.status === 401) useAuthStore.getState().logout();
    return Promise.reject(err);
  }
);
