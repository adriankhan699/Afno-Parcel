import { Route, Routes } from 'react-router';
import Layout from './components/Layout';
import BranchesPage from './pages/BranchesPage';
import HomePage from './pages/HomePage';
import NotFoundPage from './pages/NotFoundPage';
import TrackParcelPage from './pages/TrackParcelPage';

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<HomePage />} />
        <Route path="track" element={<TrackParcelPage />} />
        <Route path="track/:trackingNumber" element={<TrackParcelPage />} />
        <Route path="branches" element={<BranchesPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
