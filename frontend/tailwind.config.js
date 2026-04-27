/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        brand: {
          50: '#eef8ff',
          100: '#d9efff',
          200: '#b8e1ff',
          300: '#85cdff',
          400: '#4aafff',
          500: '#1e8dff',
          600: '#046ff5',
          700: '#0458d0',
          800: '#0b4aa3',
          900: '#0f3e82'
        }
      }
    }
  },
  plugins: []
};
