import { useState } from 'react';
import { GoogleLogin } from '@react-oauth/google';
import { api } from '../lib/api';
import { useAuthStore } from '../lib/auth';
import toast from 'react-hot-toast';
import { useNavigate } from 'react-router-dom';

export default function Login() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const setAuth = useAuthStore((s) => s.setAuth);
  const nav = useNavigate();

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setLoading(true);
    try {
      const { data } = await api.post('/auth/login', { email, password });
      setAuth(data.accessToken, {
        sellerId: data.sellerId, sellerKey: data.sellerKey,
        displayName: data.displayName, email: data.email,
        role: data.role, verticalsWhitelist: data.verticalsWhitelist ?? []
      });
      toast.success(`Hola ${data.displayName}`);
      nav('/', { replace: true });
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'Credenciales inválidas');
    } finally {
      setLoading(false);
    }
  }

  async function googleSuccess(credential?: string) {
    if (!credential) return;
    try {
      const { data } = await api.post('/auth/google', { idToken: credential });
      setAuth(data.accessToken, {
        sellerId: data.sellerId, sellerKey: data.sellerKey,
        displayName: data.displayName, email: data.email,
        role: data.role, verticalsWhitelist: data.verticalsWhitelist ?? []
      });
      toast.success(`Hola ${data.displayName}`);
      nav('/', { replace: true });
    } catch (err: any) {
      toast.error(err.response?.data?.error ?? 'Login con Google falló');
    }
  }

  return (
    <div className="min-h-screen grid place-items-center bg-slate-100">
      <div className="card w-full max-w-sm p-8">
        <h1 className="text-2xl font-bold text-center mb-1">SalesHub</h1>
        <p className="text-sm text-slate-500 text-center mb-6">Iniciá sesión para trabajar tus leads</p>
        <form onSubmit={submit} className="space-y-3">
          <input className="input" type="email" placeholder="Email"
            value={email} onChange={(e) => setEmail(e.target.value)} required />
          <input className="input" type="password" placeholder="Contraseña"
            value={password} onChange={(e) => setPassword(e.target.value)} required />
          <button className="btn-primary w-full" disabled={loading}>
            {loading ? 'Ingresando…' : 'Ingresar'}
          </button>
        </form>
        {import.meta.env.VITE_GOOGLE_OAUTH_CLIENT_ID && (
          <>
            <div className="flex items-center gap-3 my-5">
              <div className="h-px bg-slate-200 flex-1" />
              <span className="text-xs text-slate-400">o</span>
              <div className="h-px bg-slate-200 flex-1" />
            </div>
            <div className="flex justify-center">
              <GoogleLogin
                onSuccess={(cr) => googleSuccess(cr.credential)}
                onError={() => toast.error('Google OAuth falló')}
              />
            </div>
          </>
        )}
      </div>
    </div>
  );
}
