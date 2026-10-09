import { Navigate, Route, Routes, useParams } from 'react-router-dom';
import { isAdmin, useAuthStore } from './lib/auth';
import Login from './pages/Login';
import Layout from './components/Layout';
import { GoTab } from './components/TabbedPage';
import LeadDetail from './pages/LeadDetail';
import MyDashboard from './pages/MyDashboard';
import Connect from './pages/Connect';
import DashboardHub from './pages/DashboardHub';
import SellerDetail from './pages/SellerDetail';
import Conversations from './pages/Conversations';
import Manual from './pages/Manual';
import { AppsHub, CrmHub, DevicesHub, MarketingHub, TeamHub } from './pages/Hubs';

function SellerZonesRedirect() {
  const { id } = useParams<{ id: string }>();
  return <Navigate to={`/team?view=zonas&seller=${id ?? ''}`} replace />;
}

export default function App() {
  const user = useAuthStore((s) => s.user);

  if (!user) {
    return (
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    );
  }

  const admin = isAdmin(user);

  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<Navigate to={admin ? '/admin' : '/dashboard'} replace />} />
        <Route path="/dashboard" element={<MyDashboard />} />
        <Route path="/crm" element={<CrmHub />} />
        <Route path="/leads/:id" element={<LeadDetail />} />
        <Route path="/conversations" element={<Conversations />} />
        <Route path="/manual" element={<Manual />} />
        {/* El vendedor conecta su línea acá; el admin la ve dentro de Dispositivos. */}
        <Route path="/connect" element={admin ? <GoTab to="/devices" view="mi-linea" /> : <Connect />} />

        {/* Rutas viejas → tab del CRM (conservan ?source=, ?product=, …) */}
        <Route path="/leads" element={<GoTab to="/crm" view="lista" />} />
        <Route path="/pool" element={<Navigate to="/crm?view=lista&tab=pool" replace />} />
        <Route path="/leads/search" element={<GoTab to="/crm" view="captar" sub="maps" />} />
        <Route path="/leads/import" element={<GoTab to="/crm" view="captar" sub="importar" />} />
        <Route path="/map" element={<GoTab to="/crm" view={admin ? 'mapa' : 'pipeline'} />} />

        {admin && (
          <>
            <Route path="/admin" element={<DashboardHub />} />
            <Route path="/devices" element={<DevicesHub />} />
            <Route path="/apps" element={<AppsHub />} />
            <Route path="/team" element={<TeamHub />} />
            <Route path="/marketing" element={<MarketingHub />} />
            <Route path="/admin/sellers/:id" element={<SellerDetail />} />

            {/* Rutas viejas → tab de su pantalla nueva */}
            <Route path="/atencion" element={<GoTab to="/admin" view="equipo" />} />
            <Route path="/entradas" element={<GoTab to="/admin" view="ventas" />} />
            <Route path="/audio-analytics" element={<GoTab to="/admin" view="ventas" />} />
            <Route path="/pipeline" element={<GoTab to="/crm" view="captar" sub="scrapers" />} />
            <Route path="/remarketing" element={<GoTab to="/crm" view="remarketing" />} />
            <Route path="/transcripcion" element={<GoTab to="/devices" view="transcripcion" />} />
            <Route path="/voice-test" element={<GoTab to="/devices" view="voz" />} />
            <Route path="/products" element={<GoTab to="/apps" view="general" />} />
            <Route path="/diccionario" element={<GoTab to="/apps" view="bot" sub="diccionario" />} />
            <Route path="/reglas-ia" element={<GoTab to="/apps" view="bot" sub="reglas" />} />
            <Route path="/soporte" element={<GoTab to="/apps" view="bot" sub="soporte" />} />
            <Route path="/onboarding-apps" element={<GoTab to="/apps" view="bot" sub="onboarding" />} />
            <Route path="/probar-bot" element={<GoTab to="/apps" view="bot" sub="probar" />} />
            <Route path="/simulacion" element={<GoTab to="/apps" view="bot" sub="simulacion" />} />
            <Route path="/seguimientos" element={<GoTab to="/apps" view="auto" sub="seguimientos" />} />
            <Route path="/pitches" element={<GoTab to="/apps" view="auto" sub="pitches" />} />
            <Route path="/mensajeria" element={<GoTab to="/apps" view="auto" sub="mensajeria" />} />
            <Route path="/sellers" element={<GoTab to="/team" view="vendedores" />} />
            <Route path="/sellers/zones" element={<GoTab to="/team" view="zonas" />} />
            <Route path="/sellers/:id/zones" element={<SellerZonesRedirect />} />
            <Route path="/objetivos" element={<GoTab to="/team" view="objetivos" />} />
            <Route path="/posteos" element={<GoTab to="/marketing" view="posteos" sub="lista" />} />
            <Route path="/calendario" element={<GoTab to="/marketing" view="posteos" sub="calendario" />} />
            <Route path="/warmr" element={<GoTab to="/marketing" view="posteos" sub="warmr" />} />
            <Route path="/seo" element={<GoTab to="/marketing" view="seo" />} />
            <Route path="/instagram/accounts" element={<GoTab to="/marketing" view="instagram" sub="cuentas" />} />
            <Route path="/instagram/follow" element={<GoTab to="/marketing" view="instagram" sub="follow" />} />
            <Route path="/inspiracion" element={<GoTab to="/marketing" view="competencia" sub="inspiracion" />} />
            <Route path="/competitors" element={<GoTab to="/marketing" view="competencia" sub="competidores" />} />
            <Route path="/trends" element={<GoTab to="/marketing" view="competencia" sub="tendencias" />} />
          </>
        )}
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
