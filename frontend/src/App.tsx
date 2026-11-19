import { Route, Routes } from 'react-router';
import RequireAuth from './auth/RequireAuth';
import Layout from './components/Layout';
import BookParcelPage from './pages/BookParcelPage';
import BranchesPage from './pages/BranchesPage';
import HomePage from './pages/HomePage';
import LoginPage from './pages/LoginPage';
import NotFoundPage from './pages/NotFoundPage';
import ParcelDetailsPage from './pages/ParcelDetailsPage';
import ParcelsPage from './pages/ParcelsPage';
import ReceiptPage from './pages/ReceiptPage';
import RegisterPage from './pages/RegisterPage';
import ScanPage from './pages/ScanPage';
import TrackParcelPage from './pages/TrackParcelPage';

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<HomePage />} />
        <Route path="track" element={<TrackParcelPage />} />
        <Route path="track/:trackingNumber" element={<TrackParcelPage />} />
        <Route path="branches" element={<BranchesPage />} />
        <Route path="login" element={<LoginPage />} />
        <Route path="register" element={<RegisterPage />} />
        <Route
          path="book"
          element={
            <RequireAuth needsBooking>
              <BookParcelPage />
            </RequireAuth>
          }
        />
        <Route
          path="parcels"
          element={
            <RequireAuth>
              <ParcelsPage />
            </RequireAuth>
          }
        />
        <Route
          path="parcels/:id"
          element={
            <RequireAuth>
              <ParcelDetailsPage />
            </RequireAuth>
          }
        />
        <Route
          path="parcels/:id/receipt"
          element={
            <RequireAuth>
              <ReceiptPage />
            </RequireAuth>
          }
        />
        <Route
          path="scan"
          element={
            <RequireAuth needsScan>
              <ScanPage />
            </RequireAuth>
          }
        />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
